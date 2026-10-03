using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 本模组的用户设置，也就是设置界面里那几个勾选框。
    ///
    /// 设计原则：<b>核心功能不依赖任何设置项</b>。
    /// 下面这些开关全都是「增强」或者「逃生舱」，就算它们全部保持默认值，
    /// 模组的核心目标（让跨模组的床铺增益建筑通用兼容）也一定会生效。
    /// 这样设计的好处是：万一设置文件在极端情况下读取失败，也不会影响正确性 ——
    /// 最坏的结果只是少几个可选功能，而不是整个模组失灵。
    /// </summary>
    public class CompatSettings : ModSettings
    {
        /// <summary>
        /// 激进模式。
        /// 关闭（默认）：只把「能确认是床用设施」的家具纳入通用链接，
        ///               判断依据见 <see cref="CompatEngine"/> 里的分类逻辑。
        /// 开启：所有带 CompProperties_Facility 的家具都当作床用设施。
        ///       专门用来收拾那种「作者忘了把它接到任何床上、于是原本谁都连不上」的家具。
        /// </summary>
        public bool aggressiveMode;

        /// <summary>
        /// 进入地图时，对地图上已经摆着的家具做一次重新链接。
        /// 默认开启：这样老存档里原本失效的衣柜 / 屏风会立刻开始工作，不用拆掉重建。
        /// 这是一次性的开销，只在每次地图加载之后的第一个 tick（游戏逻辑步）执行。
        /// </summary>
        public bool relinkOnMapLoad = true;

        /// <summary>
        /// 忽略原版的「每个建筑最多被几个设施链接（maxSimultaneous）」这个上限。
        /// 默认关闭，因为原版的上限（通常是 1）是刻意设计的平衡手段。
        /// 开启之后，同一张床可以同时吃满好几个同种设施
        /// （比如两个衣柜各给 +0.10 舒适度，效果就能叠加了）。
        /// 注意：设施的信息面板上仍然显示原始数值，这属于「显示没跟着改」的一致性问题，
        /// 功能本身是生效的。
        /// </summary>
        public bool ignoreLinkLimit;

        /// <summary>
        /// 修正「最大链接距离比一个格子还小」的床用设施。默认开启。
        ///
        /// 背景：格子的最小间距就是 1。要是有件家具把 maxDistance 写成了 0.9 这种小于 1 的数，
        /// 那在机制上就等价于「它必须和床挤在同一格里」—— 摆在相邻格永远连不上。
        /// GloomyFurniture 的泰迪熊（GL_Teddy，maxDistance=0.9）正是这种情况，
        /// 这种数值几乎不可能是作者刻意设计的。
        ///
        /// 只会改动取值落在 (0, 1) 开区间里的设施，其它距离一律不碰
        /// （0 表示「我谁都不连」，大于等于 1 的值都是作者的有效意图）。
        /// </summary>
        public bool fixTooSmallMaxDistance = true;

        /// <summary>
        /// 输出详细的诊断日志（扫描统计、注入明细、重链耗时）。
        /// 排查「某个柜子为什么对这张床不生效」的时候，把它打开。
        /// </summary>
        public bool verboseLogging;

        /// <summary>
        /// 存档读写：RimWorld 靠这个方法把设置写进存档文件、以及从存档里读回来。
        /// 每个 Scribe_Values.Look 末尾那个默认值，必须和字段自己的初始值保持一致，
        /// 否则「老存档里没有这一项」时读出来的值，会和界面上显示的值对不上。
        /// </summary>
        public override void ExposeData()
        {
            base.ExposeData();
            // 下面每一行的三个参数分别是：要读写的字段、存档里用的键名、
            // 以及「存档里压根没有这一项时」使用的默认值。
            Scribe_Values.Look(ref aggressiveMode, "aggressiveMode", false);
            Scribe_Values.Look(ref relinkOnMapLoad, "relinkOnMapLoad", true);
            Scribe_Values.Look(ref ignoreLinkLimit, "ignoreLinkLimit", false);
            Scribe_Values.Look(ref fixTooSmallMaxDistance, "fixTooSmallMaxDistance", true);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
        }
    }
}
