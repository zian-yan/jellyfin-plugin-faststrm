using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using FastSTRM.Configuration;
using Microsoft.Extensions.Logging;

namespace FastSTRM.Playback;

/// <summary>
/// 解析「某个用户当前应该用哪套播放策略」。
/// </summary>
/// <remarks>
/// <para>
/// 优先级固定为：<b>用户级配置 &gt; 全局配置 &gt; 内置默认值</b>。
/// 用户级配置不是「一组可选开关」，而是一个模式（<see cref="PlaybackPolicyMode"/>）：
/// 选中的模式在解析时展开成 <see cref="EffectivePlaybackPolicy"/>，未覆盖的字段（补探测延迟、
/// 播放停止取消补探测）继续沿用全局值。这样任何一条用户策略都不会出现「一半是用户值、
/// 一半是默认值」的中间态。
/// </para>
/// <para>
/// 回退规则（全部退到全局策略，绝不因为一条坏配置阻断播放）：
/// 无用户上下文 → 全局；用户没有配置项 → 全局；模式为 <c>Inherit</c> → 全局；
/// 模式无法识别 → 全局并记一条 Warning；解析过程抛异常 → 全局。
/// </para>
/// <para>
/// 读取时机：取源链路（PlaybackInfo）每次现取，并把结论记为该条目「本次播放的策略」
/// （<see cref="RememberPlaybackPolicy"/>）；进度上报优先复用这条结论，
/// 因此同一次播放内不会因为会话信息缺失而改用另一套策略。
/// 配置保存后对下一次取源立即生效，不需要重启，也不影响正在播放的会话。
/// 解析结果按配置内容做指纹缓存，热路径上只多一次字典查找。
/// </para>
/// </remarks>
public sealed class PlaybackPolicyResolver
{
    /// <summary>
    /// 无效配置的告警去重上限。同一 (用户, 非法值) 只告警一次，避免每次播放都刷日志。
    /// </summary>
    private const int MaxWarnedEntries = 512;

    private const long DefaultFingerprint = 0;

    /// <summary>
    /// 内容指纹的最短重算间隔。配置对象引用没变时，最多这么久才重新比对一次内容：
    /// 播放热路径（每次 PlaybackInfo / 进度上报）不再遍历全部用户策略，
    /// 同时仍能在「同一个配置对象被原地改动」时于 1 秒内发现。
    /// </summary>
    private static readonly TimeSpan FingerprintInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 本次播放策略记录的上限。条目只用于「同一次播放内保持一致」，播放量再大也不该常驻，
    /// 因此超过上限直接清空（清空后进度上报退回会话反查，不会误判）。
    /// </summary>
    private const int MaxTrackedPlaybackPolicies = 8192;

    private readonly ILogger<PlaybackPolicyResolver> _logger;
    private readonly object _rebuildLock = new();
    private readonly ConcurrentDictionary<string, byte> _warnedEntries = new(StringComparer.Ordinal);

    /// <summary>
    /// 条目 Id → 取源时解析出的生效策略。播放进度上报没有用户参数，
    /// 不能重新解析（会话可能刚好结束，反查不到用户就会退回全局策略），
    /// 因此用取源时的结论贯穿整次播放。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, EffectivePlaybackPolicy> _playbackPolicies = new();

    /// <summary>
    /// 上一次计算内容指纹的时间（UTC ticks，0 表示从未算过）。只用于给 <see cref="FingerprintInterval"/>
    /// 提供节流依据，故意用 <see cref="Interlocked"/> 而不是锁：热路径上不能有争用。
    /// </summary>
    private long _lastFingerprintUtcTicks;

    /// <summary>
    /// 当前生效的解析快照。整对象替换（而不是原地修改），读者无需加锁。
    /// </summary>
    private volatile Snapshot _snapshot = Snapshot.FromDefaults();

    public PlaybackPolicyResolver(ILogger<PlaybackPolicyResolver> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets 当前全局策略。全局策略同时是「未配置用户」的最终结果。
    /// </summary>
    public EffectivePlaybackPolicy GlobalPolicy => GetSnapshot().Global;

    /// <summary>
    /// Gets a value indicating whether 配置文件中的策略条目因超过
    /// <see cref="PluginConfiguration.MaxUserPolicies"/> 而被截断。
    /// </summary>
    public bool PoliciesTruncated => GetSnapshot().Truncated;

    /// <summary>
    /// 解析指定用户的生效策略。
    /// </summary>
    /// <param name="userId">用户 Id；<c>null</c> 或 <see cref="Guid.Empty"/> 表示无用户上下文。</param>
    /// <returns>
    /// 生效策略。永远不会返回 <c>null</c>，也永远不会抛出：无法解析时回退全局策略。
    /// </returns>
    public EffectivePlaybackPolicy Resolve(Guid? userId)
    {
        var snapshot = GetSnapshot();

        if (userId is null || userId.Value == Guid.Empty)
        {
            return snapshot.Global;
        }

        if (!snapshot.Policies.TryGetValue(userId.Value, out var policy))
        {
            // 未单独配置：直接继承全局（Source 保持 Global，便于与「显式继承」区分）。
            return snapshot.Global;
        }

        return ResolveEntry(policy, snapshot.Global);
    }

    /// <summary>
    /// Gets 配置文件中登记的用户策略（保持配置顺序），供诊断接口与配置页展示。
    /// </summary>
    public IReadOnlyList<UserPlaybackPolicy> GetConfiguredPolicies() => GetSnapshot().Configured;

    /// <summary>
    /// 尝试取出某个用户的覆盖项（不做有效性判断，只回答「配了没有」）。
    /// </summary>
    public bool TryGetConfigured(Guid userId, out UserPlaybackPolicy? policy)
    {
        policy = null;
        return userId != Guid.Empty && GetSnapshot().Policies.TryGetValue(userId, out policy);
    }

    // ── 本次播放的策略上下文 ──

    /// <summary>
    /// 记录某条目本次播放生效的策略，由取源链路（PlaybackInfo）写入。
    /// </summary>
    /// <remarks>
    /// 记录的是**解析结果**而不是用户 Id：整次播放期间策略保持不变，
    /// 后续进度上报即使拿不到用户上下文，也不会退回全局策略而违背用户级选择。
    /// 下一次 PlaybackInfo 会覆盖这条记录，所以中间没有失效窗口。
    /// </remarks>
    public void RememberPlaybackPolicy(Guid itemId, EffectivePlaybackPolicy policy)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        if (_playbackPolicies.Count >= MaxTrackedPlaybackPolicies)
        {
            // 只是「本次播放」的旁路缓存：清空后进度上报会退回会话反查 / 全局策略。
            _playbackPolicies.Clear();
        }

        _playbackPolicies[itemId] = policy;
    }

    /// <summary>
    /// 取出某条目本次播放生效的策略。
    /// </summary>
    /// <returns><c>false</c> 表示没有记录（该条目还没走过取源链路，或记录已被清理）。</returns>
    public bool TryGetPlaybackPolicy(Guid itemId, out EffectivePlaybackPolicy policy)
    {
        if (itemId != Guid.Empty && _playbackPolicies.TryGetValue(itemId, out policy))
        {
            return true;
        }

        policy = default;
        return false;
    }

    // ── 解析 ──

    private EffectivePlaybackPolicy ResolveEntry(UserPlaybackPolicy policy, EffectivePlaybackPolicy global)
    {
        if (!PlaybackPolicyMode.TryNormalize(policy.Mode, out var mode))
        {
            if (ShouldWarn("mode:" + policy.UserId.ToString("N")))
            {
                _logger.LogWarning(
                    "FastSTRM: Invalid playback policy mode {Mode} for user {UserId}; falling back to the global playback policy",
                    LogSafeText(policy.Mode),
                    policy.UserId);
            }

            return global with
            {
                Source = PlaybackPolicySource.Fallback,
                FallbackReason = PlaybackPolicyFallbackReason.InvalidMode
            };
        }

        switch (mode)
        {
            case PlaybackPolicyMode.Inherit:
                return global with { Source = PlaybackPolicySource.Inherit };

            case PlaybackPolicyMode.OfficialProbe:
                // 官方探测路径自己会写库，补探测没有意义，因此连带关掉。
                return new EffectivePlaybackPolicy(
                    SkipProbe: false,
                    EnableBackgroundProbe: false,
                    BackgroundProbeDelaySeconds: global.BackgroundProbeDelaySeconds,
                    CancelBackgroundProbeOnPlaybackStop: global.CancelBackgroundProbeOnPlaybackStop,
                    Source: PlaybackPolicySource.User,
                    FallbackReason: null);

            case PlaybackPolicyMode.InstantNoProbe:
                return new EffectivePlaybackPolicy(
                    SkipProbe: true,
                    EnableBackgroundProbe: false,
                    BackgroundProbeDelaySeconds: global.BackgroundProbeDelaySeconds,
                    CancelBackgroundProbeOnPlaybackStop: global.CancelBackgroundProbeOnPlaybackStop,
                    Source: PlaybackPolicySource.User,
                    FallbackReason: null);

            default:
                // InstantBackgroundProbe：只有这里用得上用户级的延迟覆盖，缺省则沿用全局延迟。
                var delay = policy.BackgroundProbeDelaySeconds is int configuredDelay
                    ? Math.Clamp(configuredDelay, 0, PluginConfiguration.MaxBackgroundProbeDelaySeconds)
                    : global.BackgroundProbeDelaySeconds;

                return new EffectivePlaybackPolicy(
                    SkipProbe: true,
                    EnableBackgroundProbe: true,
                    BackgroundProbeDelaySeconds: delay,
                    CancelBackgroundProbeOnPlaybackStop: global.CancelBackgroundProbeOnPlaybackStop,
                    Source: PlaybackPolicySource.User,
                    FallbackReason: null);
        }
    }

    // ── 快照 ──

    private Snapshot GetSnapshot()
    {
        PluginConfiguration? config;
        try
        {
            config = Plugin.Instance?.Configuration;
        }
        catch (Exception ex)
        {
            // 配置读取本身出问题时也必须能播放：退回内置默认策略。
            if (ShouldWarn("config-read"))
            {
                _logger.LogWarning(
                    ex,
                    "FastSTRM: Unable to read the plugin configuration; using the built-in default playback policy");
            }

            return Snapshot.FromDefaults();
        }

        var snapshot = _snapshot;

        try
        {
            var now = DateTime.UtcNow.Ticks;

            // 快路径：保存配置一定会换成新的配置对象，因此引用未变 + 距上次内容比对不足
            // FingerprintInterval 时可以直接复用快照。热路径上只做一次引用比较和一次
            // Interlocked 读，不再遍历全部用户策略算指纹。
            if (ReferenceEquals(snapshot.Config, config)
                && now - Interlocked.Read(ref _lastFingerprintUtcTicks) < FingerprintInterval.Ticks)
            {
                return snapshot;
            }

            var fingerprint = ComputeFingerprint(config);
            Interlocked.Exchange(ref _lastFingerprintUtcTicks, now);

            // 配置对象被替换（保存配置）或内容被原地改动（手工改 XML 后重载）都会走到重建。
            if (ReferenceEquals(snapshot.Config, config) && snapshot.Fingerprint == fingerprint)
            {
                return snapshot;
            }

            return Rebuild(config, fingerprint);
        }
        catch (Exception ex)
        {
            // 配置正在被写入时 UserPolicies 可能被并发修改。沿用上一次解析成功的快照，
            // 比在播放路径上抛异常安全；下一次请求会重试。
            if (ShouldWarn("snapshot-race"))
            {
                _logger.LogWarning(
                    ex,
                    "FastSTRM: Failed to refresh the playback policy snapshot; keeping the previous one");
            }

            return snapshot;
        }
    }

    private Snapshot Rebuild(PluginConfiguration? config, long fingerprint)
    {
        lock (_rebuildLock)
        {
            var snapshot = _snapshot;
            if (ReferenceEquals(snapshot.Config, config) && snapshot.Fingerprint == fingerprint)
            {
                return snapshot;
            }

            var rebuilt = Build(config, fingerprint);
            _snapshot = rebuilt;
            return rebuilt;
        }
    }

    private Snapshot Build(PluginConfiguration? config, long fingerprint)
    {
        if (config is null)
        {
            return Snapshot.FromDefaults();
        }

        var global = config.GetGlobalPlaybackPolicy();
        var policies = new Dictionary<Guid, UserPlaybackPolicy>();
        var configured = new List<UserPlaybackPolicy>();
        var truncated = false;

        var source = config.UserPolicies;
        if (source is not null)
        {
            foreach (var policy in source)
            {
                if (policy is null)
                {
                    continue;
                }

                if (configured.Count >= PluginConfiguration.MaxUserPolicies)
                {
                    truncated = true;
                    break;
                }

                if (policy.UserId == Guid.Empty)
                {
                    if (ShouldWarn("empty-user"))
                    {
                        _logger.LogWarning("FastSTRM: Ignoring a playback policy entry with an empty user id");
                    }

                    continue;
                }

                // 同一用户重复配置时以第一条为准：顺序在 XML 与配置页里都可见且稳定，
                // 比「最后一条胜出」更容易解释。
                if (!policies.TryAdd(policy.UserId, policy))
                {
                    if (ShouldWarn("duplicate:" + policy.UserId.ToString("N")))
                    {
                        _logger.LogWarning(
                            "FastSTRM: Duplicate playback policy for user {UserId}; the first entry wins",
                            policy.UserId);
                    }

                    continue;
                }

                configured.Add(policy);
            }
        }

        if (truncated && ShouldWarn("truncated"))
        {
            _logger.LogWarning(
                "FastSTRM: More than {Max} per-user playback policies are configured; the extra entries are ignored",
                PluginConfiguration.MaxUserPolicies);
        }

        return new Snapshot(global, policies, configured, config, fingerprint, truncated);
    }

    /// <summary>
    /// 计算与播放策略相关的配置指纹（FNV-1a 64）。只覆盖参与解析的字段，
    /// 因此改「探测并发数」这类与播放无关的配置不会触发重建。
    /// </summary>
    private static long ComputeFingerprint(PluginConfiguration? config)
    {
        if (config is null)
        {
            return DefaultFingerprint;
        }

        unchecked
        {
            var hash = 14695981039346656037UL;

            Mix(ref hash, config.SkipProbeForUnprobedStrm ? 1 : 0);
            Mix(ref hash, config.EnableBackgroundProbeForUnprobedStrm ? 1 : 0);
            Mix(ref hash, config.BackgroundProbeDelaySeconds);
            Mix(ref hash, config.CancelBackgroundProbeOnPlaybackStop ? 1 : 0);

            var policies = config.UserPolicies;
            if (policies is null)
            {
                Mix(ref hash, -1);
                return (long)hash;
            }

            Mix(ref hash, policies.Count);
            foreach (var policy in policies)
            {
                if (policy is null)
                {
                    Mix(ref hash, -7);
                    continue;
                }

                Mix(ref hash, policy.UserId.GetHashCode());
                Mix(ref hash, policy.Mode is null ? 0 : StringComparer.Ordinal.GetHashCode(policy.Mode));
                Mix(ref hash, policy.BackgroundProbeDelaySeconds ?? int.MinValue);
            }

            return (long)hash;
        }
    }

    private static void Mix(ref ulong hash, long value)
    {
        unchecked
        {
            hash ^= (ulong)value;
            hash *= 1099511628211UL;
        }
    }

    // ── 日志去重 ──

    /// <summary>
    /// 配置值来自手工编辑的文件，可能含控制字符，落日志前先中和，避免伪造日志行。
    /// </summary>
    private static string LogSafeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var chars = (value.Length <= 64 ? value : value[..64] + "…").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    private bool ShouldWarn(string key)
    {
        if (_warnedEntries.Count >= MaxWarnedEntries)
        {
            // 只是日志去重标记，清空只会让某条告警之后再出现一次。
            _warnedEntries.Clear();
        }

        return _warnedEntries.TryAdd(key, 0);
    }

    /// <summary>
    /// 不可变解析快照。<see cref="Config"/> 与 <see cref="Fingerprint"/> 一起构成缓存键。
    /// </summary>
    private sealed class Snapshot
    {
        private static readonly IReadOnlyList<UserPlaybackPolicy> EmptyConfigured =
            Array.Empty<UserPlaybackPolicy>();

        public Snapshot(
            EffectivePlaybackPolicy global,
            Dictionary<Guid, UserPlaybackPolicy> policies,
            IReadOnlyList<UserPlaybackPolicy> configured,
            PluginConfiguration? config,
            long fingerprint,
            bool truncated)
        {
            Global = global;
            Policies = policies;
            Configured = configured;
            Config = config;
            Fingerprint = fingerprint;
            Truncated = truncated;
        }

        public EffectivePlaybackPolicy Global { get; }

        public IReadOnlyDictionary<Guid, UserPlaybackPolicy> Policies { get; }

        public IReadOnlyList<UserPlaybackPolicy> Configured { get; }

        public PluginConfiguration? Config { get; }

        public long Fingerprint { get; }

        public bool Truncated { get; }

        public static Snapshot FromDefaults()
            => new(
                EffectivePlaybackPolicy.Defaults,
                new Dictionary<Guid, UserPlaybackPolicy>(),
                EmptyConfigured,
                null,
                DefaultFingerprint,
                false);
    }
}
