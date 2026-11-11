using System;

namespace FastSTRM.Probing;

/// <summary>
/// 补全远端文件大小的触发来源。
/// </summary>
/// <remarks>
/// <para>
/// 「条目缺大小」只是**必要条件**，不是触发条件：同一个缺大小的条目，在「官方探测」「不探测」
/// 策略下、在取源（PlaybackInfo）与浏览详情页上都不该补全，只有在入库自动探测、起播后补探测、
/// 手动增量探测、手动全量探测这四个场景才补全。旧实现的触发判断只看「是否缺大小」，
/// 判定结论取决于调用点是否**恰好**做过别的检查，任何一个新调用点都能造成误触发。
/// </para>
/// <para>
/// <see cref="None"/> 固定为 0：任何没有显式给出来源的调用（默认值、强转、未来新增的调用点
/// 忘记传参）都落在「不允许补全」一侧，误触发在类型层面就不可能发生。
/// </para>
/// </remarks>
public enum RemoteSizeTrigger
{
    /// <summary>
    /// 未列明场景：官方探测、不探测、取源（PlaybackInfo）、浏览详情页、统计等，一律不补全。
    /// </summary>
    None = 0,

    /// <summary>入库自动探测（<c>ILibraryManager.ItemAdded</c>，需开启「入库自动探测」）。</summary>
    ItemAdded = 1,

    /// <summary>起播后补探测（仅「跳过探测 · 后台补探测」策略）。</summary>
    PlaybackProbe = 2,

    /// <summary>手动增量探测（<c>POST /FastStrm/Probe/Start?mode=Incremental</c>）。</summary>
    ManualIncrementalProbe = 3,

    /// <summary>手动全量探测（<c>POST /FastStrm/Probe/Start?mode=Full</c>）。</summary>
    ManualFullProbe = 4,
}

/// <summary>
/// 触发来源的判定规则：**唯一**决定「哪些场景允许补全远端文件大小」的地方。
/// </summary>
/// <remarks>
/// 白名单写在 <see cref="AllowsSizeFill"/> 一处，调用点、执行入口与测试都引用它，
/// 因此新增一个探测链路时必须显式在这里登记，否则不会补全（默认拒绝）。
/// </remarks>
public static class RemoteSizeTriggerPolicy
{
    /// <summary>
    /// 是否允许该来源补全远端文件大小。只有四个已登记的探测场景返回 <c>true</c>，
    /// 其余（<see cref="RemoteSizeTrigger.None"/>、未定义的枚举值）一律返回 <c>false</c>。
    /// </summary>
    public static bool AllowsSizeFill(RemoteSizeTrigger trigger)
        => trigger is RemoteSizeTrigger.ItemAdded
            or RemoteSizeTrigger.PlaybackProbe
            or RemoteSizeTrigger.ManualIncrementalProbe
            or RemoteSizeTrigger.ManualFullProbe;

    /// <summary>
    /// 该来源是否要求「即使已有可信大小也重新解析」。
    /// </summary>
    /// <remarks>
    /// 全量探测的契约是「每个字段都用新探测的数据覆盖」，所以它重新解析远端大小；
    /// 增量探测、入库自动探测与起播后补探测都只补「仍然缺失」的大小，
    /// 不得覆盖已有记录。这里把 <c>force</c> 从来源推导出来，避免再出现一个可以与之矛盾的参数。
    /// </remarks>
    public static bool RequiresRefresh(RemoteSizeTrigger trigger)
        => trigger == RemoteSizeTrigger.ManualFullProbe;

    /// <summary>
    /// 用于日志的来源名称（枚举被强转成未定义值时也不会抛异常）。
    /// </summary>
    public static string Describe(RemoteSizeTrigger trigger) => trigger switch
    {
        RemoteSizeTrigger.ItemAdded => nameof(RemoteSizeTrigger.ItemAdded),
        RemoteSizeTrigger.PlaybackProbe => nameof(RemoteSizeTrigger.PlaybackProbe),
        RemoteSizeTrigger.ManualIncrementalProbe => nameof(RemoteSizeTrigger.ManualIncrementalProbe),
        RemoteSizeTrigger.ManualFullProbe => nameof(RemoteSizeTrigger.ManualFullProbe),
        RemoteSizeTrigger.None => nameof(RemoteSizeTrigger.None),
        _ => "Undefined(" + (int)trigger + ")"
    };
}
