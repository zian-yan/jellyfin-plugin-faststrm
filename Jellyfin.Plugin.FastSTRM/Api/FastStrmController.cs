using System;
using System.Collections.Generic;
using System.Linq;
using FastSTRM.Configuration;
using FastSTRM.Playback;
using FastSTRM.Probing;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FastSTRM.Api;

/// <summary>
/// API endpoints for FastSTRM probe management. Admin-only.
/// </summary>
[ApiController]
[Route("FastStrm")]
[Authorize(Policy = "RequiresElevation")]
public class FastStrmController : ControllerBase
{
    private readonly ProbeService _probeService;
    private readonly PlaybackPolicyResolver _policyResolver;
    private readonly IUserManager _userManager;

    public FastStrmController(
        ProbeService probeService,
        PlaybackPolicyResolver policyResolver,
        IUserManager userManager)
    {
        _probeService = probeService;
        _policyResolver = policyResolver;
        _userManager = userManager;
    }

    /// <summary>
    /// Returns a summary of .strm items in the library.
    /// </summary>
    /// <param name="refresh">Bypasses the 30s cache, e.g. right after a probe finished.</param>
    [HttpGet("Summary")]
    public ActionResult GetSummary([FromQuery] bool refresh = false)
    {
        var (total, probed, unprobed, sizeMissing) = _probeService.GetSummary(refresh);

        // 这里返回的是**全局**播放策略。按用户区分的生效策略见 /FastStrm/PlaybackPolicy。
        var global = _policyResolver.GlobalPolicy;
        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();

        return Ok(new
        {
            Total = total,
            Probed = probed,
            Unprobed = unprobed,
            // 仍是 .strm 文本文件大小的条目数（远端真实大小尚未写入）
            SizeMissing = sizeMissing,
            SkipProbeForUnprobedStrm = global.SkipProbe,
            EnableBackgroundProbeForUnprobedStrm = global.EnableBackgroundProbe,
            BackgroundProbeDelaySeconds = global.BackgroundProbeDelaySeconds,
            CancelBackgroundProbeOnPlaybackStop = global.CancelBackgroundProbeOnPlaybackStop,
            EnableRemoteSize = configuration.EnableRemoteSize,
            RemoteSizeTimeoutSeconds = configuration.GetRemoteSizeTimeoutSeconds(),
            AllowPrivateNetworkTargets = configuration.AllowPrivateNetworkTargets
        });
    }

    /// <summary>
    /// Returns the global playback policy plus, for every user, the policy that actually applies
    /// and where it came from (user-level override, explicit inherit, or the global fallback).
    /// </summary>
    /// <param name="userId">Optional filter: resolve a single user only.</param>
    [HttpGet("PlaybackPolicy")]
    public ActionResult GetPlaybackPolicies([FromQuery] Guid? userId)
    {
        var configured = _policyResolver.GetConfiguredPolicies();

        var users = new List<object>();
        var knownUserIds = new HashSet<Guid>();

        foreach (var user in _userManager.GetUsers())
        {
            knownUserIds.Add(user.Id);

            if (userId.HasValue && userId.Value != user.Id)
            {
                continue;
            }

            users.Add(BuildUserPolicyView(user));
        }

        // 配置里登记了、但 Jellyfin 已经没有这个用户：保留条目但不参与解析，
        // 在这里单独列出来，避免「策略存在却永远不生效」无从排查。
        var orphans = configured
            .Where(policy => !knownUserIds.Contains(policy.UserId))
            .Select(policy => new
            {
                policy.UserId,
                policy.Mode,
                policy.BackgroundProbeDelaySeconds
            })
            .ToList();

        return Ok(new
        {
            Global = ToPolicyView(_policyResolver.GlobalPolicy),
            Users = users,
            Orphans = orphans,
            PoliciesTruncated = _policyResolver.PoliciesTruncated
        });
    }

    /// <summary>
    /// Returns the effective playback policy for a single user (or the global policy when
    /// <paramref name="userId"/> is omitted). Useful for troubleshooting "why did this user get
    /// the official probe / a background probe?".
    /// </summary>
    [HttpGet("PlaybackPolicy/Effective")]
    public ActionResult GetEffectivePlaybackPolicy([FromQuery] Guid? userId)
    {
        return Ok(ToPolicyView(_policyResolver.Resolve(userId)));
    }

    /// <summary>
    /// Starts a probe task. Mode: "Incremental" or "Full".
    /// </summary>
    [HttpPost("Probe/Start")]
    public ActionResult StartProbe([FromQuery] string mode = ProbeService.IncrementalMode)
    {
        // Reject unknown modes instead of letting them fall through to a full-library rescan.
        if (!ProbeService.TryParseMode(mode, out var normalizedMode))
        {
            return BadRequest(new
            {
                Message = $"Unsupported mode '{mode}'. Use '{ProbeService.IncrementalMode}' "
                          + $"or '{ProbeService.FullMode}'."
            });
        }

        if (_probeService.StartProbe(normalizedMode))
        {
            return NoContent();
        }

        return Conflict(new { Message = "A probe task is already running or still stopping." });
    }

    /// <summary>
    /// Stops the currently running probe task.
    /// </summary>
    [HttpPost("Probe/Stop")]
    public ActionResult StopProbe()
    {
        _probeService.StopProbe();
        return NoContent();
    }

    /// <summary>
    /// Returns the current probe task status and progress.
    /// </summary>
    [HttpGet("Probe/Status")]
    public ActionResult GetProbeStatus()
    {
        return Ok(new
        {
            _probeService.State,
            Processed = _probeService.ProcessedItems,
            Total = _probeService.TotalItems,
            CurrentItem = _probeService.CurrentItemName
        });
    }

    // ── Helpers ──

    private object BuildUserPolicyView(User user)
    {
        var hasEntry = _policyResolver.TryGetConfigured(user.Id, out var configured);
        var effective = _policyResolver.Resolve(user.Id);

        string? configuredMode = hasEntry ? configured?.Mode : null;

        // 未配置时 configuredMode 为 null，TryNormalize 同样返回 false，无需额外分支。
        var modeIsValid = PlaybackPolicyMode.TryNormalize(configuredMode, out var normalizedMode);
        var inheritsExplicitly = modeIsValid
                                 && string.Equals(normalizedMode, PlaybackPolicyMode.Inherit, StringComparison.Ordinal);

        return new
        {
            UserId = user.Id,
            UserName = GetUserName(user),
            HasOverride = hasEntry && modeIsValid && !inheritsExplicitly,
            ConfiguredMode = configuredMode,
            ConfiguredModeValid = hasEntry && modeIsValid,
            ConfiguredDelaySeconds = hasEntry ? configured?.BackgroundProbeDelaySeconds : null,
            Effective = ToPolicyView(effective)
        };
    }

    private static object ToPolicyView(EffectivePlaybackPolicy policy)
        => new
        {
            policy.SkipProbe,
            policy.EnableBackgroundProbe,
            policy.BackgroundProbeDelaySeconds,
            policy.CancelBackgroundProbeOnPlaybackStop,
            Mode = policy.Mode,
            policy.Source,
            policy.FallbackReason
        };

    private string GetUserName(User user)
    {
        try
        {
            var dto = _userManager.GetUserDto(user, null);
            if (!string.IsNullOrEmpty(dto?.Name))
            {
                return dto.Name;
            }
        }
        catch (Exception)
        {
            // 名字只用于展示，取不到就退回 Id，绝不能因此让诊断接口 500。
        }

        return user.Id.ToString();
    }
}
