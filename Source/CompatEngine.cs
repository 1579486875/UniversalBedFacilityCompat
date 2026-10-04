using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 兼容引擎 —— 本模组真正干活的代码全都在这个类里。其它几个文件只是「点火开关」
    /// （CompatBootstrap）、「设置界面和补丁安装」（CompatMod）、「进游戏后跑腿收尾」
    /// （CompatGameComponent）而已。
    ///
    /// 【为什么需要它：原版的机制有个「只认一边」的毛病】
    /// 在 RimWorld 1.6 里，一件家具（衣柜、屏风、床头柜这类）能作用于哪些床，
    /// 不是家具自己说了算的。CompProperties_Facility.linkableBuildings 这个字段
    /// 既不从 XML 里读、也不会存进存档（它标了 [Unsaved]，属于「用时现算」的临时数据），
    /// 而是游戏在加载 Def 的时候，由 ResolveReferences() 反过来倒推出来的：
    ///
    ///     linkableBuildings = { 所有 Def D | D.linkableFacilities 中包含我自己 }
    ///
    /// 翻成白话：想知道「这个衣柜能给哪些床加成」，只能挨个去翻每张床手里的名单
    /// linkableFacilities（意思是「这张床欢迎哪些家具来影响我」），
    /// 谁的名单上写了这个衣柜，这个衣柜才算能作用于谁。
    /// 也就是说 —— 「谁认谁」这份名录完全由「床那一侧」单方面决定，家具这边一句话都说不上。
    ///
    /// 【名词别搞混：「推导」和「建链」是两回事】
    ///   · 推导（就是上面那段）：床侧名单 → 反推出设施侧名单，发生在 Def 加载期；
    ///   · 建链（真正决定加成能不能吃到）：运行时由「设施那一侧」驱动 ——
    ///     游戏遍历设施自己的 linkableBuildings，对每个候选调用 CanLinkTo 判定，
    ///     通过才建立实际链接。床侧的 linkableFacilities 只服务于
    ///     「放置时的预览连线」和「刚放下那一瞬间的自动连接」。
    ///   所以本模组两手都要抓：既要把设施写进床侧名单（好推导出正确的设施侧名单），
    ///   也要保证设施侧名单本身完整 —— 两者缺一，实际链接就会少掉一块。
    ///
    /// 麻烦正出在这儿：做家具的模组通常只把这件家具写进原版 Bed / DoubleBed 的名单里，
    /// 压根不认识别的模组新加的那些床。于是同一个衣柜，对原版床有效，
    /// 对模组床就彻底失灵 —— 这就是本模组要修的问题。
    ///
    /// 【修复流程：六个步骤，按顺序做】
    ///   1. 分类   —— 把全部 Def 扫一遍，挑出所有床（def.IsBed 为真）和所有设施
    ///                （身上挂 CompProperties_Facility 的家具），并给它们各存一份
    ///                「本模组还没动手时」的原始快照，也就是基线
    ///   2. 判定   —— 认出哪些家具属于「床用设施」（设计上就是用来伺候床的那些），
    ///                顺便把那些用自定义 thingClass 写的、def.IsBed 认不出来的「伪床」
    ///                也收编进来；这两件事互相依赖（认床要用设施当线索，认设施又要用床当线索），
    ///                所以得来回迭代几轮，直到一轮下来没有任何新发现为止
    ///   3. 注入   —— 把判定出的床用设施，挨个写进每一张床的 linkableFacilities 名单里；
    ///                同时把「本模组动过谁」记进「并集」（历史累计名单），
    ///                并把这次不再处理、上一次却动过的建筑还原成基线原样
    ///   4. 重建   —— 按原版那套算法把设施的 linkableBuildings 重算一遍
    ///                （用并集思路，只增不减，绝不抹掉别人写进去的东西）
    ///   5. 上限   —— 按玩家设置，可选地放开 maxSimultaneous（一件设施最多同时链接几个建筑）
    ///   6. 距离   —— 按玩家设置，可选地把「最大链接距离小于一格」这种写坏了的数值
    ///                抬到能覆盖相邻格的范围
    ///
    /// 除了这六步，还有两件事不属于「扫描」本身：
    ///
    ///   「重链」—— 把上面那些在 Def 数据上做的改动，落实到地图上**已经摆好**的家具身上。
    ///   原因：物体之间实际的链接关系，是建筑被放下去的那一刻（SpawnSetup）当场建立的，
    ///   不会写进存档。所以光改 Def 数据救不了老存档里已经摆着的那批旧家具，
    ///   必须主动让它们重新连一次。触发时机有两个：
    ///   · 载入存档之后，由 CompatGameComponent 在第一个 tick（游戏的一帧逻辑步）触发一次；
    ///   · 玩家在设置界面点「立即重新扫描」时立刻触发一次。
    ///   它遍历的是 everInjectedFacilities 这份并集，所以连「这次已经不算数」的设施
    ///   也能被翻出来，解开它身上残留的旧链接。
    ///
    ///   「完整性自检」—— 由 CompatGameComponent 在第一个 tick 调用一次，见
    ///   <see cref="EnsureLinkTablesIntact"/>。它防的是这种局面：本模组跑完之后，
    ///   又有别的模组把同一批名单整个重建了一遍。因为带 [StaticConstructorOnStartup]
    ///   的静态构造函数是由 StaticConstructorOnStartupUtility.CallAll() 挨个触发的，
    ///   而那个顺序**不保证**与本模组的加载顺序一致 —— 在本模组之后被触发的模组，
    ///   完全可能把名单改成它自己认识的样子，把本模组刚做完的成果悄悄覆盖掉。
    ///
    ///   （关于这个顺序的确切情况，2026-10-04 用反编译核实过，以免后人照着错的印象改：
    ///    CallAll 自身是**串行**的 foreach + RunClassConstructor，每个类型各有 try/catch；
    ///    真正「不保证」的是它拿到的那份类型列表 ——
    ///    GenTypes.AllTypesWithAttribute 里写的是 AsParallel().Where(...).ToList()，
    ///    用 PLINQ 做筛选。PLINQ 的 ToList 会保留源顺序，所以最终顺序
    ///    = GenTypes.AllTypes 的顺序 ≈ 程序集加载顺序，**与模组列表顺序并不等同**。
    ///    本文件与 CompatGameComponent 里早先对此的描述互相矛盾，现统一以这一段为准。）
    ///
    /// 全程不写死任何具体 DefName（不针对某个模组硬编码），
    /// 也不挂在 Tick / Update / 渲染这些每帧都要跑的路径上。
    ///
    /// 【关于「基线」：为什么要留原始快照】
    /// 四张基线表都只在第一次扫描时采集，之后永远不更新。它们有两个用途：
    ///   · 判定时拿它当依据 —— 免得把本模组自己刚注入的东西，误当成
    ///     「作者本来就把它连到床上了」的证据（那样会自我循环，越判越多）；
    ///   · 重新扫描时拿它做重建的起点 —— 保证玩家反复开关设置也不会一层层累积污染，
    ///     每次都能干干净净回到最初的样子，再按当前设置重新来一遍。
    /// </summary>
    public static class CompatEngine
    {
        /// <summary>开启「忽略链接上限」后，把上限拉到这个数（只拉大、不缩小）。999 约等于「随便连」，只有玩家勾选了对应开关时才用得上。</summary>
        private const int LinkLimitOverrideValue = 999;

        /// <summary>「认设施」和「认伪床」这两件事最多来回迭代几轮。实测两轮就已经稳定不变了，这里给足余量。</summary>
        private const int MaxClassifyPasses = 4;

        /// <summary>
        /// 保险丝（<see cref="OnFacilityResolveReferences"/>）出错时的日志去重键。
        /// 必须与 CompatGameComponent 里那几个键取值不同 —— Log.ErrorOnce 是按 key 去重的，
        /// 两个不同来源共用同一个 key，后来的那条会被悄悄吃掉。
        /// </summary>
        private const int FuseFailedErrorKey = 0x55424645;

        /// <summary>
        /// 「玩家手动重扫时某张地图重链失败」的日志去重键。
        /// 与进游戏时那次重链（CompatGameComponent 的 0x55424644）分开，
        /// 让两条路径各自都能报出第一条错误。
        /// </summary>
        private const int RelinkMapErrorKey = 0x55424646;

        /// <summary>
        /// 详细报告里最多列出几个 defName，多出来的会折叠成「…(+N)」。
        ///
        /// 这个数不宜大：设置窗口是固定尺寸、既不能缩放也没有滚动条，
        /// 列太多会被直接裁掉，反而一整段都看不到。8 个足够确认「它认出了哪些」。
        /// </summary>
        private const int ReportSampleLimit = 8;

        /// <summary>
        /// 报告文本的折行宽度（按字符数算，中英文一视同仁）。
        /// 为什么要折行：报告里的 defName 清单是用「、」连起来的、中间没有空格，
        /// 按词折行的机制对它并不可靠；而设置窗口固定尺寸、既不能缩放也没有滚动条，
        /// 一行太长会被直接裁掉，反而整段都看不见。这里按字符数主动折行更稳。
        /// </summary>
        private const int ReportLineWrapWidth = 68;

        // ───────────────────────── 状态 ─────────────────────────

        /// <summary>初始化是否已经成功跑完。Harmony 那层保险丝、以及重新链接地图的逻辑，都看这个开关决定要不要开工。</summary>
        public static bool Ready { get; private set; }

        /// <summary>
        /// 「本模组初始化过几次」的世代号，每调用一次 Initialize() 就加一。
        ///
        /// 用途：CompatGameComponent 靠它判断玩家是不是点了设置界面里的「重新扫描」。
        /// 一旦发现它变了，就把自检的「已检查过」「已试几次」一起清零 ——
        /// 否则自检一旦试满 MaxIntactAttempts 次就永久停摆，玩家手动重扫也救不回来。
        /// </summary>
        internal static int InitializeGeneration { get; private set; }

        /// <summary>这一轮初始化有没有真的改动过链接名单。没改动就说明本来就一切正常，后续的重链也就不必白忙。</summary>
        public static bool LinkTableChanged { get; private set; }

        /// <summary>
        /// 玩家设置的快捷通道，省得每次都写一遍 CompatMod.Settings。
        ///
        /// 注意：这里不做任何「万一为空」的兜底，直接把 CompatMod.Settings 转交给调用方。
        /// 敢这么写是因为：Mod 对象（也就是 Settings 被赋值的那一刻）在 Def 加载之前就创建好了，
        /// 正常流程下它一定不为空；但为了保险起见，所有用到它的地方都统一写成
        /// `Settings?.xxx ?? 默认值`（意思是「有设置就用设置，没有就用默认值」），
        /// 由调用方自己兜底（见 CollectExclusions 等处）。
        /// </summary>
        internal static CompatSettings Settings => CompatMod.Settings;

        // 本次扫描的结果。每一对都是「List + HashSet」两人搭档：
        // List 负责保持稳定的先后顺序（报告和日志按它输出，看起来才不会跳来跳去），
        // HashSet 负责「某个东西在不在里面」的快速查询（无论有多少项，一次就能查完）。
        // 它们在每次 Initialize 开头都会被清空重建。
        private static readonly List<ThingDef> bedDefs = new List<ThingDef>();
        private static readonly HashSet<ThingDef> bedDefSet = new HashSet<ThingDef>();
        private static readonly List<ThingDef> bedFacilityDefs = new List<ThingDef>();
        private static readonly HashSet<ThingDef> bedFacilitySet = new HashSet<ThingDef>();
        private static readonly List<FacilityRecord> facilityRecords = new List<FacilityRecord>();

        /// <summary>
        /// 「历史累计：本模组曾经当成床用设施处理过的家具」总名单。
        ///
        /// 重新链接地图时必须遍历这份总名单，而不是只看这一次判定的结果。原因在于：
        /// 玩家关掉激进模式再点一次重新扫描时，像 ToolCabinet（工作台工具柜）这种
        /// 「上次算数、这次不算数」的家具，在本次判定里已经找不到了，
        /// 可它和床之间**已经建立起来**的那些实物链接还挂在那儿 ——
        /// CompAffectedByFacilities.GetStatOffset（计算加成时会被调用的方法）
        /// 只管把 linkedFacilities 里的每一条累加起来，压根不会回头确认
        /// 「白名单里现在还有没有它」，于是「取消」就成了空话。
        /// 只有遍历这份总名单，才能把它们身上残留的链接解开。
        /// </summary>
        private static readonly HashSet<ThingDef> everInjectedFacilities = new HashSet<ThingDef>();

        /// <summary>
        /// 「历史累计：本模组曾经当成床、往里注入过设施的建筑」总名单（真床和被收编的伪床都算）。
        ///
        /// 要它的理由是让「退场」的建筑能干净还原：InjectIntoLinkingSide 只遍历**这一次**
        /// 判定出来的床，所以像「激进模式下被收编、关掉开关后又不算数了」的伪床，
        /// 会带着上一次留下的一堆注入永久赖在游戏里。有了这份总名单，
        /// RestoreRetiredBeds 就能把它们恢复成基线的样子。
        /// </summary>
        private static readonly HashSet<ThingDef> everInjectedBeds = new HashSet<ThingDef>();

        /// <summary>原始快照：本模组还没动手时，每张床的 linkableFacilities 长什么样（值为 null 表示原版本来就是 null）。</summary>
        private static readonly Dictionary<ThingDef, List<ThingDef>> bedBaseline =
            new Dictionary<ThingDef, List<ThingDef>>();

        /// <summary>原始快照：本模组还没动手时，每件设施的 linkableBuildings 长什么样。</summary>
        private static readonly Dictionary<ThingDef, List<ThingDef>> facilityBaseline =
            new Dictionary<ThingDef, List<ThingDef>>();

        /// <summary>原始快照：每件设施原本的 maxSimultaneous（最多同时链接几个建筑），用于关掉「忽略链接上限」时原样还回去。</summary>
        private static readonly Dictionary<ThingDef, int> maxSimultaneousBaseline =
            new Dictionary<ThingDef, int>();

        /// <summary>原始快照：每件设施原本的 maxDistance（最大链接距离），用于关掉「修正过小距离」时原样还回去。</summary>
        private static readonly Dictionary<ThingDef, float> maxDistanceBaseline =
            new Dictionary<ThingDef, float>();

        /// <summary>
        /// 「最大链接距离小于一个格子」的设施，会被抬到这个目标值（单位：格子）。
        /// 为什么定 2 格：够得着紧挨着的格子（连斜角也覆盖得到），又不至于夸张到跨过整间屋子。
        /// </summary>
        private const float ReasonableMaxDistance = 2f;

        // 本次扫描的统计数字。只用来拼设置界面的报告和写日志，不参与任何判断，
        // 所以就算数字统计得不准，也不会影响功能。
        private static int injectedLinkCount;
        private static int rebuiltFacilityCount;
        private static int bedsWithoutCompCount;
        private static int absorbedPseudoBedCount;

        /// <summary>
        /// 既被当成床、又被当成床用设施的 Def 有几个（正常情况下是 0）。
        ///
        /// 这类「床本身也是一种床上家具」的 Def 会把自己写进自己的白名单，
        /// 结果就是一张床给自己加舒适度。这不是本模组搞出来的机制
        /// （原版 CanLinkTo 本来就不禁止自己连自己），但本模组让
        /// 「每张床的白名单都包含全部床用设施」成了必然结果，
        /// 于是这种情况就从「理论上可能」变成了「一定会发生」。
        /// 这里统计出来是为了让它在详细日志里能被看见；而**自己连自己**那一条
        /// 已经在注入与还原两处被主动切断了（见 InjectIntoLinkingSide 与
        /// OnFacilityResolveReferences 里的自连跳过）。
        /// 注意只切「自己 → 自己」这一条：同一种家具之间互相加成是原版语义，那个不能动。
        /// </summary>
        /// <summary>
        /// 下面这些静态集合（基线表、并集名单、各种缓存）**只在 Unity 主线程被访问**，
        /// 所以刻意没有加锁。这不是疏忽，而是 RimWorld 的运行约定：
        ///   ·Initialize() 由 [StaticConstructorOnStartup] 触发，在 DoPlayLoad 的主线程上跑；
        ///   ·CompatGameComponent 的 tick、设置窗口的绘制，同样都在主线程；
        ///   ·本模组挂的保险丝在 CompProperties_Facility.ResolveReferences 上
        ///     （它是 ThingDef.ResolveReferences 串行往下调的一环），而那条路径是
        ///     parallel:false 的（真并行的是 ThingCategoryDef / RecipeDef，它们不经过本补丁）。
        /// 如果将来要把这些逻辑挪到后台线程，必须先把这些集合全部加锁 —— 否则 List / Dictionary
        /// 在并发写时会静默丢数据或抛异常。
        /// </summary>
        private static int overlappingDefCount;

        /// <summary>「Def 数据库为空」只报一次，免得连点重扫时刷屏。</summary>
        private static bool emptyDatabaseLogged;

        /// <summary>「链接表被篡改」只报一次，理由同上。</summary>
        private static bool tablesTamperedLogged;

        /// <summary>
        /// 完整性自检时临时借用一下的集合（见 <see cref="EnsureLinkTablesIntact"/>）。
        /// 做成静态字段、每次清空复用，而不是每次检查都新建一个，
        /// 是为了让第一个 tick 那次检查不额外地产生垃圾对象。
        /// </summary>
        private static readonly HashSet<ThingDef> intactCheckScratch = new HashSet<ThingDef>();

        /// <summary>
        /// 保险丝路径（OnFacilityResolveReferences）里用来判断
        /// 「这一项是不是已经在名单里」的临时集合。
        ///
        /// 为什么不直接用 List.Contains：那条路径的外层循环里，
        /// 名单会一路增长到「全部床 Def」，于是每次查找都是 O(床Def数)，
        /// 整体退化成 O(床Def数²)；而整个方法还会被**每一件床用设施各调用一次**。
        /// HashSet 把每次查找降到 O(1)。
        ///
        /// ⚠ 为什么「降成 O(1) 之后语义仍然完全不变」——理由比看上去绕，别记错：
        ///
        /// 早先这里写的是「ThingDef 没有重写 Equals，所以两者都是引用比较」，
        /// **这个理由是错的**（2026-10-04 复审查出）。实际情况是：
        ///   · Verse.Def **实现了 IEquatable&lt;Def&gt;**，
        ///   · 也**重写了 GetHashCode()**（返回 defNameHash）；
        ///   · 但 ThingDef **没有实现 IEquatable&lt;ThingDef&gt;**。
        ///
        /// 而 IEquatable&lt;T&gt; 是**不协变**的，所以
        /// EqualityComparer&lt;ThingDef&gt;.Default 判定「ThingDef 是否可当作
        /// IEquatable&lt;ThingDef&gt;」得到的答案是**否**，于是它退化成
        /// ObjectEqualityComparer —— 走 object.Equals(object)，也就是**引用比较**。
        /// List&lt;ThingDef&gt;.Contains 走的是同一条 EqualityComparer&lt;T&gt;.Default 路径，
        /// 所以两者严格同义。等价性成立，只是原因不同。
        ///
        /// 记这一条的实际意义：这个等价性**依赖泛型参数恰好是 ThingDef**。
        /// 哪天有人把这里改成 HashSet&lt;Def&gt; 或 List&lt;Def&gt;，
        /// 判等方式会**当场**变成「defNameHash + GetType 判等」，
        /// 而那种变化不会有任何编译错误、也不会有运行时报错，只会静悄悄地改变行为。
        ///
        /// 复用同一个实例，是为了避免每次调用都分配一个新集合。
        /// 它只在单次调用内使用、用完即清（见 Core 里的用法），不跨调用保留任何数据。
        /// </summary>
        private static readonly HashSet<ThingDef> fuseScratch = new HashSet<ThingDef>();

        /// <summary>
        /// 以前有没有初始化过至少一次。用来分清「游戏启动时的第一次扫描」
        /// 和「玩家在游戏里手动点重新扫描」—— 后者需要立刻把地图上已有的家具也刷新一遍。
        /// 在 Initialize 最开头就置位，免得中途提前 return 了，下次被误当成第一次。
        /// </summary>
        private static bool everInitialized;

        /// <summary>设置界面报告的缓存文本（连缓存时详细日志开关的状态一起记着），免得设置窗口每帧都重新拼一遍字符串。</summary>
        private static string reportCache;
        private static bool reportCacheVerbose;

        /// <summary>
        /// 一个共享的空名单，只在比较的时候充当「什么都没有」的基线（配合 ?? 给可能为 null 的基线兜底）。
        ///
        /// 为什么需要它：基线快照是允许存 null 的（原版 XML 没写 linkableFacilities / linkableBuildings
        /// 时就是 null），而 SameSequence 遇到 null 一律判「不一样」。于是「基线还没建立（null）、
        /// 当前也确实是空表」这种其实没变化的组合，会被当成「和现在不一样」，
        /// 每次重扫都白置一次 LinkTableChanged、多触发一次全地图重链。
        /// 语义上 null 与空表在「有没有东西要连」这件事上完全等价，归一化不会改变任何实际行为。
        ///
        /// ⚠️ 这个对象是只读共享的：绝不能往里 Add，也绝不能把它赋给任何真正的链接名单
        ///（否则同一个对象被多处共用，一处 Clear 就会把别处一起清空）。
        /// </summary>
        private static readonly List<ThingDef> EmptyDefList = new List<ThingDef>();

        /// <summary>一件设施的扫描记录：它是哪个 Def、它的组件配置在哪、以及它动手前的原始样子。</summary>
        private sealed class FacilityRecord
        {
            public ThingDef def;
            public CompProperties_Facility props;

            /// <summary>原始快照（本模组动手之前的 linkableBuildings），用来判断「它算不算床用设施」，也用来做并集合并。</summary>
            public List<ThingDef> originalBuildings;
        }

        // ───────────────────────── 入口 ─────────────────────────

        /// <summary>
        /// 由 <see cref="CompatBootstrap"/> 在全部 Def 加载完成之后调用，是整个引擎的总入口。
        /// 可以放心重复调用（设置界面的「立即重新扫描」按钮就是这么用的）：
        /// 里面每一步都是「先还原成基线、再按当前设置重新做一遍」，
        /// 所以重复跑不会越积越多，也不会把上一次的改动留在里面。
        /// </summary>
        public static void Initialize()
        {
            // 第一次走进来时 everInitialized 还是 false，说明这是游戏启动时的自动扫描；
            // 之后再进来就一定是玩家手动点的「重新扫描」（只有手动重扫才需要顺手刷新地图）。
            bool isManualRescan = everInitialized;
            everInitialized = true;

            try
            {
                ResetRunState();

                List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;
                if (allDefs == null || allDefs.Count == 0)
                {
                    // Def 列表是空的，说明游戏还没把数据读完（正常情况下不会发生）。
                    // 这时候什么都做不了，只能记一条警告然后收工。
                    // 只报一次：「立即重新扫描」按钮会反复触发本方法，
            // 而「Def 数据库为空」不会因为多等一会儿就变好，重复报只是刷屏。
            if (!emptyDatabaseLogged)
            {
                emptyDatabaseLogged = true;
                Log.Warning("[UBFC] " + "UBFC_Log_EmptyDefDatabase".Translate());
            }
                    return;
                }

                HashSet<ThingDef> excludedFacilities = CollectExclusions(p => p.excludedFacilities);
                HashSet<ThingDef> excludedBeds = CollectExclusions(p => p.excludedBeds);

                // 下面这七行就是修复流程的六个步骤（第 2 步的「认设施 + 认伪床」合在一起跑）。
                // 顺序不能乱：必须先有分类和基线，才谈得上判定；先判定完，才谈得上注入；
                // 注入完了才能按新名单去重建设施那边的表。
                CollectFacilities(allDefs, excludedFacilities);
                CollectBeds(allDefs, excludedBeds);
                ClassifyAndAbsorb(allDefs, excludedBeds);
                InjectIntoLinkingSide();
                RebuildFacilityLinkTables();
                ApplyLinkLimitOverride();
                ApplyMaxDistanceFix();

                Ready = true;
                LogSummary();
            }
            catch (Exception ex)
            {
                // 出异常意味着 Def 数据可能只改了一半（半成品比没改更麻烦），
                // 这时候绝不能对外声称「我准备好了」—— 宁可把 Ready 打回 false，
                // 让依赖它的功能（Harmony 保险丝、重链）先停下，也不要带着半成品继续跑。
                Ready = false;
                Log.Error("[UBFC] " + "UBFC_Log_InitFailed".Translate() + "\n" + ex);
                return;
            }

            // 「刷新地图」这一步是故意放在上面那个 try 之外的，而且自己又套了一层 catch。
            // 原因：走到这里时 Def 数据已经改完了，而且改下去就收不回来。
            // 万一刷新地图失败，绝不能把 Ready 打回 false ——
            // 那会连带把 Harmony 保险丝和组件首个 tick 的重链一起废掉，
            // 等于因为一件小事没办成，把已经办好的大事也一并否定了。
            if (isManualRescan)
            {
                try
                {
                    RelinkAllMapsIfPlaying();
                }
                catch (Exception ex)
                {
                    Log.Error("[UBFC] " + "UBFC_Log_RelinkFailed".Translate() + "\n" + ex);
                }
            }

            // 世代号 +1：告诉 CompatGameComponent「这是一轮新的扫描」。
            // 它会据此把自检状态清零（见 InitializeGeneration 的说明），
            // 这样玩家在设置界面点「重新扫描」之后，完整性自检还能重新跑起来。
            InitializeGeneration++;
        }

        /// <summary>
        /// 清空「本次扫描」产生的临时状态，为新一轮扫描做准备（相当于把桌面擦干净重新摆）。
        ///
        /// 有一样东西是特意<b>不</b>清的：那些记录「跨次调用历史」的结构。
        /// 四张基线表要继续保持「本模组从没碰过」的原始状态，
        /// 两个「本模组动过谁」的总名单要继续记着旧账 ——
        /// 它们正是「重新扫描能精确回到原点」「退场的建筑能干净还原」这两件事的依据。
        /// </summary>
        private static void ResetRunState()
        {
            Ready = false;
            LinkTableChanged = false;
            bedDefs.Clear();
            bedDefSet.Clear();
            bedFacilityDefs.Clear();
            bedFacilitySet.Clear();
            facilityRecords.Clear();
            injectedLinkCount = 0;
            rebuiltFacilityCount = 0;
            bedsWithoutCompCount = 0;
            absorbedPseudoBedCount = 0;
            overlappingDefCount = 0;
            reportCache = null;

            // 注意：下面这些结构是特意不在这里清空的，它们记的是跨次调用的历史 ——
            //   bedBaseline / facilityBaseline：本模组没碰过的原始状态，
            //       既要用它做判定，也要用它做「重新扫描时的精确回退」；
            //   maxSimultaneousBaseline / maxDistanceBaseline：同样是原始状态，
            //       分别服务于第 5 步（链接上限）和第 6 步（链接距离）；
            //   everInjectedFacilities / everInjectedBeds：本模组动过谁的总名单，
            //       用来解开那些已经退场的对象身上残留的修改。
        }

        // ───────────────────────── 阶段 1：分类与基线 ─────────────────────────

        /// <summary>
        /// 从「兼容策略 Def」（就是那个留给玩家和模组作者的逃生舱）里读出排除名单，
        /// 也就是「这些家具我不要」「这些床我不要」两份黑名单。
        ///
        /// 会把**所有**策略 Def 的名单合并到一起 —— 这样第三方模组再新增一个策略 Def 时，
        /// 谁先加载、谁后加载都不影响最后的结果。（如果只读第一个策略 Def，
        /// 后来者写进去的东西就会被无声无息地忽略掉，那这个逃生舱对最需要它的人反而失效了。）
        /// </summary>
        private static HashSet<ThingDef> CollectExclusions(Func<CompatPolicyDef, List<ThingDef>> selector)
        {
            var result = new HashSet<ThingDef>();

            List<CompatPolicyDef> policies = DefDatabase<CompatPolicyDef>.AllDefsListForReading;
            if (policies == null || policies.Count == 0)
            {
                // 一个策略 Def 都没有（玩家把 Defs 文件夹删了之类的极端情况），
                // 那就当作「没有任何排除项」处理，返回一个空名单。
                return result;
            }

            for (int p = 0; p < policies.Count; p++)
            {
                List<ThingDef> list = selector(policies[p]);
                if (list == null)
                {
                    continue;
                }
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                    {
                        result.Add(list[i]);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 第 1 步（前半）：把「凡是挂着 CompProperties_Facility 这个组件的 Def」全收集起来 ——
        /// 这类东西就是「设施」，也就是有可能给床加成的家具。顺手给每一件存一份原始快照。
        /// </summary>
        private static void CollectFacilities(List<ThingDef> allDefs, HashSet<ThingDef> excluded)
        {
            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef def = allDefs[i];
                if (def == null || excluded.Contains(def))
                {
                    // 在排除名单里的家具直接跳过 —— 这就是「逃生舱」发挥作用的地方。
                    continue;
                }

                // GetCompProperties<T>() 内部是用「is T」（是不是这类东西）来判断的，
                // 所以别的模组自己派生出来的子类（例如 MyFacilityProps : CompProperties_Facility）
                // 同样会被这里命中，不会被漏掉。
                CompProperties_Facility props = def.GetCompProperties<CompProperties_Facility>();
                if (props == null)
                {
                    continue;
                }

                if (!facilityBaseline.TryGetValue(def, out List<ThingDef> snapshot))
                {
                    // 只有第一次扫描才会走到这里。此刻本模组还没往任何地方注入过东西，
                    // 所以这份快照是纯正的「原版自己推导出来的结果」，可以放心当基线用。
                    // （存的时候是复制出一份新列表，而不是把原列表直接拿来引用，
                    //   这样以后原列表被改动，也不会把这份基线一起改脏。）
                    snapshot = props.linkableBuildings == null
                        ? null
                        : new List<ThingDef>(props.linkableBuildings);
                    facilityBaseline[def] = snapshot;
                }

                // 下面两个也只在第一次遇到这件设施时记录，记的是它「原本的数值」，
                // 供第 5 步和第 6 步将来做还原用。
                if (!maxSimultaneousBaseline.ContainsKey(def))
                {
                    maxSimultaneousBaseline[def] = props.maxSimultaneous;
                }

                if (!maxDistanceBaseline.ContainsKey(def))
                {
                    maxDistanceBaseline[def] = props.maxDistance;
                }

                facilityRecords.Add(new FacilityRecord
                {
                    def = def,
                    props = props,
                    originalBuildings = snapshot
                });
            }
        }

        /// <summary>
        /// 第 1 步（后半）：把所有的床也收集起来，同样各存一份原始快照。
        /// 1.6 判断「这东西是不是床」的办法，是看它的 thingClass 是不是 Building_Bed
        /// 或者它的子类（也就是 def.IsBed 这个属性干的事）。只要模组床还老实继承原版的床类，
        /// 这一关就认得出来；用自定义 thingClass 写的「伪床」则要靠第 2 步去认。
        /// </summary>
        private static void CollectBeds(List<ThingDef> allDefs, HashSet<ThingDef> excluded)
        {
            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef def = allDefs[i];
                if (def == null || excluded.Contains(def) || !def.IsBed)
                {
                    // 不是床、或者被玩家列进了黑名单，就跳过。
                    continue;
                }

                CompProperties_AffectedByFacilities affected =
                    def.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (affected == null)
                {
                    // 这张床没挂 CompAffectedByFacilities 这个「我愿意被设施影响」的组件，
                    // 也就是说从机制上设施根本挂不到它身上 —— 这是床模组自己的责任，
                    // 本模组再怎么努力也救不了。这里只把数量记下来，回头在日志和报告里提醒玩家。
                    bedsWithoutCompCount++;
                    continue;
                }

                if (!bedBaseline.ContainsKey(def))
                {
                    // 同样是复制出一份新列表当快照，而不是把原列表拿来直接引用。
                    bedBaseline[def] = affected.linkableFacilities == null
                        ? null
                        : new List<ThingDef>(affected.linkableFacilities);
                }

                // HashSet.Add 返回 true，意思是「原来没有它，这次真的加进去了」。
                // 只有这种情况才需要往 List 里也放一份 —— 这样两个容器的内容始终一一对应。
                if (bedDefSet.Add(def))
                {
                    bedDefs.Add(def);
                }
            }
        }

        // ───────────────────────── 阶段 2：判定（迭代） ─────────────────────────

        /// <summary>
        /// 第 2 步：「认床用设施」和「收编伪床」这两件事来回做，直到谁也不再有新发现为止。
        ///
        /// 为什么要来回做？因为这两件事互相依赖，先有鸡还是先有蛋说不清。举个真实的例子：
        /// 家具 F 既挂在原版床上、又挂在某张用自定义 thingClass 写的床 P 上。
        /// 第一轮时，F 靠「原版床的名单里有它」被判成了床用设施，可 P 这时候还不算「床」；
        /// 等 P 被收编进来之后，第二轮就能顺着 F 这条线索把 P 也认出来了。
        /// （下面说的「判据 A」，指的就是「它的名单里出现过床」这条线索。）
        ///
        /// 但要说明的是，有一种死角是迭代<b>解不开</b>的：家具 F <b>只</b>挂在伪床 P 上，
        /// 自己又不带任何「我必须在床边上」之类的约束标记。这种局面下第一轮什么设施都认不出来，
        /// bedDefs.Count 一动不动，循环立刻就退出了 —— 这不是缺陷，而是刻意选择的保守：
        /// 没有任何证据能证明 F 是床用设施，硬猜的话会把工作台的工具柜也一起卷进来。
        /// 这种死角交给「激进模式」兜底：它把候选范围放宽到全部设施，
        /// 同时仍然用 <see cref="IsWorkTableLike"/> 把工作台那一类建筑挡在门外。
        /// </summary>
        private static void ClassifyAndAbsorb(List<ThingDef> allDefs, HashSet<ThingDef> excludedBeds)
        {
            bool aggressive = Settings?.aggressiveMode ?? false;

            for (int pass = 0; pass < MaxClassifyPasses; pass++)
            {
                // 记下这一轮开始前有多少张床，用来判断「这一轮到底有没有新收获」。
                int bedsBefore = bedDefs.Count;

                // 这个返回值是只靠判据 A / B 得出的「高置信度」名单，激进模式影响不到它。
                HashSet<ThingDef> confident = ClassifyFacilities(aggressive);
                AbsorbPseudoBeds(allDefs, excludedBeds, confident, aggressive);

                if (bedDefs.Count == bedsBefore)
                {
                    // 这一轮一张新床都没认出来，说明结果已经稳定，再迭代下去也是白跑一趟。
                    break;
                }
            }
        }

        /// <summary>
        /// 跑一轮「这件家具算不算床用设施」的判断。三条线索里只要中了一条就算数：
        ///   A. 它动手前的名单（基线）里至少出现过一张床（包括前几轮刚收编的伪床）
        ///      —— 这条最可靠，说明作者确实把它接到床上去了
        ///   B. 它带着「必须摆在床边上」这类约束标记 —— 原版床头柜（EndTable）用的正是
        ///      mustBePlacedAdjacentCardinalToBedHead 这个字段
        ///   C. 玩家开了激进模式（可选）—— 一律当成床用设施，专门用来收拾那些
        ///      「作者压根忘了把它接到任何床上」的家具
        ///
        /// 返回值只包含由 A / B 判出来的「高置信度」名单，是留给下一步（收编伪床）用的 ——
        /// 认伪床时不能用激进模式放宽过的范围，否则会一传十、十传百地连锁误判。
        /// 原版的工作台工具柜（ToolCabinet）三条一条都不中，所以不会被误伤。
        /// </summary>
        private static HashSet<ThingDef> ClassifyFacilities(bool aggressive)
        {
            var confident = new HashSet<ThingDef>();
            bedFacilityDefs.Clear();
            bedFacilitySet.Clear();

            for (int i = 0; i < facilityRecords.Count; i++)
            {
                FacilityRecord rec = facilityRecords[i];
                if (rec?.def == null || rec.props == null)
                {
                    // 记录不完整（理论上不该发生），跳过它，别让一条坏数据把整轮扫描带崩。
                    continue;
                }

                // 线索 B：带着「必须摆在床边上」这类约束标记。
                // 这三个字段里任意一个为真，都说明这件家具的用途跟床绑死了。
                bool isConfident = rec.props.mustBePlacedAdjacentCardinalToBedHead
                                   || rec.props.mustBePlacedAdjacentCardinalToAndFacingBedHead
                                   || rec.props.canLinkToMedBedsOnly;

                // 线索 A：它动手前的名单里出现过床。
                // （bedDefSet 里已经包含前几轮收编进来的伪床，所以这一条也能顺着伪床认出新设施。）
                if (!isConfident && rec.originalBuildings != null)
                {
                    for (int j = 0; j < rec.originalBuildings.Count; j++)
                    {
                        ThingDef building = rec.originalBuildings[j];
                        if (building != null && (building.IsBed || bedDefSet.Contains(building)))
                        {
                            isConfident = true;
                            break;
                        }
                    }
                }

                if (isConfident)
                {
                    confident.Add(rec.def);
                }

                // 线索 C：激进模式。它只影响「最后把哪些设施算进来」，
                // 不参与伪床判断 —— 否则会把一堆普通家具也连带着认成床。
                if (isConfident || aggressive)
                {
                    if (bedFacilitySet.Add(rec.def))
                    {
                        bedFacilityDefs.Add(rec.def);
                    }
                }
            }

            return confident;
        }

        /// <summary>
        /// 收编「伪床」：有些模组的「床」是用自定义 thingClass 写的，def.IsBed 认不出它。
        /// 但也不能见到像的就收 —— 必须同时满足两件事：它<b>确实具备床的特征</b>，
        /// 而且它动手前的名单里写着我们正在找的那批候选设施。
        ///
        /// 四道刻意设下的护栏（每一道都是踩过坑之后才加的）：
        /// · <b>必须具备床的特征</b>（<see cref="LooksLikeBed"/>）—— 防连锁误判的关键，详见那个方法；
        /// · 判断依据用基线、不用当前值 —— 否则第二次扫描时会把本模组自己上次注入的东西
        ///   当成「作者本来就写了」的证据，越认越多；
        /// · 排除工作台类建筑 —— 原版工作台也带 CompAffectedByFacilities（指向 ToolCabinet），
        ///   不挡的话它就会被当成一张床；
        /// · 排除名单优先 —— 逃生舱必须拦得住这一类床，否则它在最需要它的场合反而失效。
        ///
        /// 候选范围会随模式变化：默认模式只用高置信度的 A/B 结果（保守），
        /// 激进模式放宽到全部设施 —— 这样遇上「家具只挂在伪床上、用途无从确认」的死锁，
        /// 玩家打开激进模式就能解开。
        /// </summary>
        private static void AbsorbPseudoBeds(List<ThingDef> allDefs, HashSet<ThingDef> excludedBeds,
            HashSet<ThingDef> confidentFacilities, bool aggressive)
        {
            HashSet<ThingDef> candidates = aggressive ? bedFacilitySet : confidentFacilities;
            if (candidates.Count == 0)
            {
                // 一件候选设施都没有，那就没有任何线索可以顺藤摸瓜，直接收工。
                return;
            }

            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef def = allDefs[i];
                if (def == null || def.IsBed || bedDefSet.Contains(def))
                {
                    // 是空 Def、本来就认得出来的真床、或者前几轮已经收编过的，都跳过。
                    continue;
                }
                if (excludedBeds.Contains(def) || IsWorkTableLike(def) || !LooksLikeBed(def))
                {
                    // 三道护栏一次过筛：玩家明确排除的、长得像工作台的、不具备床的特征的，都不收。
                    continue;
                }

                CompProperties_AffectedByFacilities affected =
                    def.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (affected?.linkableFacilities == null)
                {
                    // 连「我愿意被设施影响」的组件都没有、或者名单是空的，
                    // 那就没有线索可查，跳过。
                    continue;
                }

                // 判断时优先用基线（本模组动手前的样子）；只有在完全没有基线记录时
                // （也就是第一次遇到它）才退而求其次用当前值 —— 那种情况下两者本来就相等。
                List<ThingDef> judge = bedBaseline.TryGetValue(def, out List<ThingDef> baseLine)
                    ? baseLine
                    : affected.linkableFacilities;
                if (judge == null)
                {
                    continue;
                }

                for (int j = 0; j < judge.Count; j++)
                {
                    ThingDef fac = judge[j];
                    if (fac == null || !candidates.Contains(fac))
                    {
                        continue;
                    }

                    if (bedDefSet.Add(def))
                    {
                        bedDefs.Add(def);
                        if (!bedBaseline.ContainsKey(def))
                        {
                            // 以前没给它存过快照，这里现补一份。存的是「当前值」——
                            // 能走到这个分支，说明上面没能用上基线，也就意味着它还没被本模组动过，
                            // 当前值和基线本来是同一份内容，存下来是安全的。
                            bedBaseline[def] = new List<ThingDef>(affected.linkableFacilities);
                        }
                        absorbedPseudoBedCount++;
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 判断一个建筑是不是「工作台那一类」。工作台身上同样挂着 CompAffectedByFacilities
        /// （原版是拿它来连工具柜 ToolCabinet 的），但它伺候的是生产活儿，不是睡觉，
        /// 所以绝不能把它当成床收编。
        ///
        /// 注意这道护栏<b>管不到研究台</b>：原版的研究台 Building_ResearchBench 是直接继承
        /// Building 的，并不是 Building_WorkTable 的子类，所以这里的判断认不出它来。
        /// 研究台那边的把关，交给 <see cref="LooksLikeBed"/> 去做。
        /// </summary>
        private static bool IsWorkTableLike(ThingDef def)
        {
            return def.thingClass != null && typeof(Building_WorkTable).IsAssignableFrom(def.thingClass);
        }

        /// <summary>
        /// 判断一个建筑是不是<b>真的具备床的特征</b>，相当于收编伪床之前的资格审核。
        ///
        /// 为什么非要有这道审核 —— 这是本模组实打实踩过的一个坑：
        /// 最早收编伪床的条件只有「不是 IsBed + 不像工作台 + 名单里有候选设施」，太宽松了。
        /// 漏洞出在研究台身上：原版 Building_ResearchBench 直接继承 Building，
        /// <b>不是</b> Building_WorkTable 的子类，所以 IsWorkTableLike 根本拦不住它。
        /// 而研究台同样带 CompAffectedByFacilities（原版用它来挂研究增益设施）。
        /// 只要它的名单里出现某件「两边都能用」的通用设施（既伺候床、也可能伺候研究台），
        /// 它就会被错当成「床」收编进来；紧接着在下一轮判断里，
        /// 那些伺候研究台的家具又会顺着线索 A 被连带判成「床用设施」——
        /// 最后的结果就是：研究专用的家具被注入到了所有床的名单里。
        ///
        /// 所以这里改成用「床专属」的特征来把关。下面两条，真正的床必定有，
        /// 而研究台 / 工作台 / 其它功能建筑必定没有：
        ///   · 挂着 CompAssignableToPawn_Bed —— 原版 BedBase 用的就是它，有了它床才能指派给小人；
        ///   · building 里开了床专属的字段（是否显示睡姿 / 每天治疗量）。
        ///
        /// 要说明的是：真正的 Building_Bed 子类根本走不到这里（前面 def.IsBed 那一关已经拦掉了），
        /// 所以这道审核只用来筛「自称是床的候选者」，卡得严一点完全合理。
        /// </summary>
        private static bool LooksLikeBed(ThingDef def)
        {
            // 特征 1：床专属的「可指派」组件
            CompProperties_AssignableToPawn assignable =
                def.GetCompProperties<CompProperties_AssignableToPawn>();
            if (assignable?.compClass != null
                && typeof(CompAssignableToPawn_Bed).IsAssignableFrom(assignable.compClass))
            {
                return true;
            }

            // 特征 2：床专属的建筑数据（少数床可能用的是基类 CompAssignableToPawn，就靠这条兜底）
            BuildingProperties building = def.building;
            if (building != null && (building.bed_showSleeperBody || building.bed_healPerDay > 0f))
            {
                return true;
            }

            // 两条都不中，那就不能当成床。宁可漏收，也不能错收 ——
            // 错收一张假床，会把一堆不相干的家具连同它的名单一起卷进来。
            return false;
        }

        // ───────────────────────── 阶段 3：注入 ─────────────────────────

        /// <summary>
        /// 第 3 步的主入口，按顺序做三件事：
        ///   1. 把这次判定出来的床用设施<b>全部</b>登记进 everInjectedFacilities 总名单
        ///      （注意是「全部」，而不是「这次真正写进去了新条目的那些」）；
        ///   2. 还原那些「以前被当成床处理过、这次不再处理」的建筑（见 RestoreRetiredBeds）；
        ///   3. 逐张床注入 —— 把床用设施写进每张床的 linkableFacilities 名单。
        ///
        /// 第 3 步用的是「从基线重建」而不是「直接往后追加」：先把名单原地清空、
        /// 把基线内容写回去，再把设施追加进去。这样重复调用（比如关掉激进模式后重新扫描）
        /// 能精确回到原点，不会残留上一次的结果；而且自始至终用的是同一个 List 对象
        /// （只是清空再重填），不会把「手里正握着这个名单引用」的其他代码搞懵。
        /// 计算量是 O(床数 × 设施数)，只在启动或玩家手动重扫时跑一次。
        /// </summary>
        private static void InjectIntoLinkingSide()
        {
            // 先统计「既是床、又是床用设施」的 Def 有几个。这段特意和下面的注入逻辑分开写，
            // 免得读代码的人误以为这个计数参与了注入决策 —— 它只是喂给详细日志看的。
            // 用直接赋值而不是累加：本方法可能被完整性自检再调用一次，累加会算出双倍的数字。
            overlappingDefCount = 0;
            for (int i = 0; i < bedDefs.Count; i++)
            {
                if (bedFacilitySet.Contains(bedDefs[i]))
                {
                    overlappingDefCount++;
                }
            }

            // 先把这次判定出的床用设施**全部**纳入管理总名单 —— 判断依据是
            // 「它算不算床用设施」，而不是「这次有没有真的写进去新条目」。
            //
            // 后者是个陷阱：老存档里作者本来就接好的设施（比如早就挂在原版床上的模组衣柜）
            // 这次不会产生任何新条目，要是因此把它排除在总名单之外，
            // RelinkMap 就不会去刷新它，它对新加进来的模组床依然无效 ——
            // 那正好把本模组的核心功能给废掉了。
            // 这么做同时也让「上次算数、这次不算数」的设施（关掉激进模式后的工具柜）
            // 留在总名单里，将来重链时能顺手把已经建立的残留链接解开。
            for (int j = 0; j < bedFacilityDefs.Count; j++)
            {
                everInjectedFacilities.Add(bedFacilityDefs[j]);
            }

            // 再把「以前被当成床处理过、这次不再处理」的建筑还原回去 ——
            // 比如关掉激进模式后不再被收编的伪床。还原必须从基线来，
            // 否则本模组上一次留下的注入会永远赖在它身上。
            RestoreRetiredBeds();

            if (bedDefs.Count == 0)
            {
                // 一张床都没有（比如没装任何床模组、也没启用任何加床的模组），
                // 那注入自然无从谈起，直接收工。
                return;
            }

            var existing = new HashSet<ThingDef>();
            for (int i = 0; i < bedDefs.Count; i++)
            {
                ThingDef bed = bedDefs[i];
                CompProperties_AffectedByFacilities affected =
                    bed.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (affected == null)
                {
                    // 这张床连「我愿意被设施影响」的组件都没有，跳过。
                    continue;
                }

                List<ThingDef> list = affected.linkableFacilities;
                if (list == null)
                {
                    // 名单是 null 的时候，原版会直接「什么都不链接」地结束
                    // （相当于名单为空且永远不打算填）。
                    // 换成一张新的空列表之后，这张床才有资格参与设施链接。
                    list = new List<ThingDef>();
                    affected.linkableFacilities = list;
                    LinkTableChanged = true;
                }

                if (bedBaseline.TryGetValue(bed, out List<ThingDef> baseLine))
                {
                    // 有基线：先把当前名单清空，再把基线原样写回去 ——
                    // 这就是「每次重扫都从原点重新来一遍」的做法。
                    // （顺手把基线里的 null 项过滤掉，免得把空条目也复制进去。）
                    list.Clear();
                    if (baseLine != null)
                    {
                        for (int j = 0; j < baseLine.Count; j++)
                        {
                            if (baseLine[j] != null)
                            {
                                list.Add(baseLine[j]);
                            }
                        }
                    }
                }
                else if (Settings?.verboseLogging ?? false)
                {
                    // 理论上不可能出现的状况：每张床在收集阶段都一定会被记录下基线。
                    // 真遇上了也刻意不清空名单 —— 宁可少一次「回到原点」的机会，
                    // 也绝不冒险把原版数据弄丢。
                    Log.Warning("[UBFC] " + "UBFC_Log_MissingBedBaseline".Translate(bed.defName));
                }

                if (bedFacilityDefs.Count == 0)
                {
                    // 一件设施都没有，那这张床也就没什么可注入的，继续处理下一张。
                    continue;
                }

                // 把名单里已有的内容先装进一个集合，下面追加时用它来去重，
                // 免得同一件设施在名单里出现两遍。
                existing.Clear();
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j] != null)
                    {
                        existing.Add(list[j]);
                    }
                }

                for (int j = 0; j < bedFacilityDefs.Count; j++)
                {
                    ThingDef fac = bedFacilityDefs[j];

                    // 跳过「它自己」。
                    //
                    // 有些 Def 既是床、又是床用设施（两个组件挂在了同一个 Def 上）。
                    // 原版判定「这个设施能不能连到这张床」时，自己做自己的距离是 0，
                    // 距离检查必然通过，于是它会把自己写进自己的名单 ——
                    // 表现出来就是「这张床凭空多出一份自己提供的加成」。
                    //
                    // 原版本身并不拦这种自连（它只看看设施那一侧）。那本来属于
                    // 「理论上可能发生」；可本模组会把全库设施注入到每一张床上，
                    // 于是「可能」就变成了「必然」。所以这里主动切断
                    // 「这张床 → 它自己」这一条。
                    //
                    // 注意只切这一条：其它床照样把该 Def 当设施用，不受影响。
                    if (ReferenceEquals(fac, bed))
                    {
                        continue;
                    }

                    if (existing.Add(fac))
                    {
                        // Add 返回 true 说明「原来没有它」，也就是这次真的新增了一条链接。
                        list.Add(fac);
                        injectedLinkCount++;
                        LinkTableChanged = true;
                    }
                }

                // 把这张床记进「本模组动过谁」的总名单，将来它退场时才好还原。
                everInjectedBeds.Add(bed);
            }
        }

        /// <summary>
        /// 把「以前被本模组当成床注入过、这次却不再处理」的建筑，其名单按基线还原回去。
        /// 这是 InjectIntoLinkingSide 只遍历本次 bedDefs 的必要补充 ——
        /// 少了它，那些已经退场的建筑（比如关掉激进模式后不再被收编的伪床）
        /// 就会带着上一次的注入永远留在游戏里。
        /// </summary>
        private static void RestoreRetiredBeds()
        {
            if (everInjectedBeds.Count == 0)
            {
                // 从来没往任何一张床里注过东西，也就没有历史包袱，直接收工。
                return;
            }

            foreach (ThingDef retired in everInjectedBeds)
            {
                if (retired == null || bedDefSet.Contains(retired))
                {
                    // 这次依然算数（还在 bedDefSet 里），那就不叫「退场」，跳过。
                    continue;
                }

                CompProperties_AffectedByFacilities affected =
                    retired.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (affected?.linkableFacilities == null)
                {
                    // 连名单都没有（或组件已经没了），没什么可还原的。
                    continue;
                }
                if (!bedBaseline.TryGetValue(retired, out List<ThingDef> baseLine))
                {
                    // 找不到它的基线，那就不知道原本长什么样。
                    // 与其瞎猜，不如什么都不做 —— 保持不变总比改错强。
                    continue;
                }

                // 内容已经和基线一模一样，就什么都不用做。
                // 少了这一句，只要还存在退场床，每次 Initialize（包括自检触发的重跑）
                // 都会把 LinkTableChanged 立起来，平白多触发一次全地图重链。
                //
                // baseLine 可能是 null（原版本来就没有这个名单，见 bedBaseline 的存法）：
                // 这时不能拿 null 去比 —— SameSequence 见到 null 一律返回 false，
                // 「基线还没建立（null）」就会被当成「和现在不一样」，
                // 于是每次重扫都白触发一次全地图重链。归一化成空表即可：
                // null 与空表在「有没有东西要连」这件事上完全等价，比出来相同就是真的什么都没变。
                if (SameSequence(affected.linkableFacilities, baseLine ?? EmptyDefList))
                {
                    continue;
                }

                // 用基线内容整体替换当前名单（同样是原地清空再重填）。
                // 这里刻意用「替换」而不是像 RebuildFacilityLinkTables 那样取并集：
                // 本方法的目的是让退场的建筑彻底回到原样，取并集会把本模组注入进去的
                // 那些设施留下来，等于没还原。
                affected.linkableFacilities.Clear();
                if (baseLine != null)
                {
                    for (int j = 0; j < baseLine.Count; j++)
                    {
                        if (baseLine[j] != null)
                        {
                            affected.linkableFacilities.Add(baseLine[j]);
                        }
                    }
                }
                LinkTableChanged = true;
            }
        }

        // ───────────────────────── 阶段 4：重建 ─────────────────────────

        /// <summary>
        /// 第 4 步：按原版 ResolveReferences 的那套算法，把设施这边的 linkableBuildings 重算一遍。
        /// 和原版比有三处不同，每一处都是为了更安全或者更快：
        ///   1. 改成「顺着每栋建筑的名单扫一遍 → 往设施那边归集」的单遍扫描 + 哈希去重，
        ///      把计算量从「设施数 × 全部 Def 数」降到「全部 Def 数 × 名单长度」。
        ///      算出来的内容与先后顺序和原版完全一致（同样按 AllDefsListForReading 的次序追加）。
        ///   2. 先把「身上带 CompProperties_Facility 的 Def」收成一个集合，再进内层循环做判断，
        ///      免得在内层一遍遍地调用没有缓存的 GetCompProperties（原因见方法内的注释）。
        ///   3. 合并时取并集：重算结果里没有、但基线里本来就有的条目会被保留下来，
        ///      这样别的模组用自定义子类塞进去的东西不会被我们抹掉。
        /// </summary>
        private static void RebuildFacilityLinkTables()
        {
            List<ThingDef> allDefs = DefDatabase<ThingDef>.AllDefsListForReading;

            // 先把「真正挂着 CompProperties_Facility 的 Def」装进一个集合，后面直接查它。
            //
            // 为什么非要先做这一步：GetCompProperties<T>() 在 ThingDef 上并没有缓存，
            // 它每调用一次都要从头**把 comps 列表挨个翻一遍**。要是把它留在内层循环里
            // 逐件设施去判断，计算量会变成「建筑数 × 名单长度 × comps 数量」——
            // 而我们注入之后，一张床的名单最长能有上百项，那就慢得没必要了。
            // 收成集合之后，内层的这一步判断就退化成一次哈希查找。
            //
            // 顺便说明为什么这么做不会改变结果：这个集合的来源是 facilityRecords，
            // 而最后把结果写回去的那一遍本来就只遍历 facilityRecords，
            // 所以排除名单里的设施没进这个集合，也不会影响这里的输出。
            //
            // 但请注意：那只保证「这里算得对」，并不等于「退出名单的设施就没事了」——
            // 它们的链接表会停在上一次注入之后的样子，需要本方法末尾的
            // RestoreRetiredFacilities 来收拾。详见那个方法的说明。
            var defsWithFacilityComp = new HashSet<ThingDef>();
            for (int i = 0; i < facilityRecords.Count; i++)
            {
                ThingDef facDef = facilityRecords[i]?.def;
                if (facDef != null)
                {
                    defsWithFacilityComp.Add(facDef);
                }
            }

            // 第一遍：顺着「建筑 → 它名单里的设施」这个方向扫，
            // 反过来归集成「设施 → 有哪些建筑愿意连它」。
            // table 存结果，dedup 负责去重（同一栋建筑不要重复记两次）。
            var table = new Dictionary<ThingDef, List<ThingDef>>();
            var dedup = new Dictionary<ThingDef, HashSet<ThingDef>>();

            for (int i = 0; i < allDefs.Count; i++)
            {
                ThingDef buildingDef = allDefs[i];
                if (buildingDef == null)
                {
                    continue;
                }

                CompProperties_AffectedByFacilities affected =
                    buildingDef.GetCompProperties<CompProperties_AffectedByFacilities>();
                if (affected?.linkableFacilities == null || affected.linkableFacilities.Count == 0)
                {
                    // 这栋建筑没有「愿意被设施影响」的组件，或者名单是空的，
                    // 那它对任何设施都不感兴趣，跳过。
                    continue;
                }

                for (int j = 0; j < affected.linkableFacilities.Count; j++)
                {
                    ThingDef facilityDef = affected.linkableFacilities[j];
                    if (facilityDef == null || !defsWithFacilityComp.Contains(facilityDef))
                    {
                        // 空条目、或者这件设施根本不在我们的记录里（比如被玩家排除了），
                        // 都不参与重建。
                        continue;
                    }

                    if (!table.TryGetValue(facilityDef, out List<ThingDef> list))
                    {
                        // 第一次遇到这件设施，给它开一份新的结果列表和去重集合。
                        list = new List<ThingDef>();
                        table[facilityDef] = list;
                        dedup[facilityDef] = new HashSet<ThingDef>();
                    }

                    if (dedup[facilityDef].Add(buildingDef))
                    {
                        // 只有「这栋建筑还没记过」才追加，保证同一栋建筑只出现一次。
                        list.Add(buildingDef);
                    }
                }
            }

            // 第二遍：把结果真正写到 props 实例上。
            // 按 props（组件配置对象）而不是按单个 Def 来归并，是因为好几个 Def
            // 有可能共用同一个 CompProperties 实例，这样也不会互相覆盖。
            var merged = new Dictionary<CompProperties_Facility, List<ThingDef>>();
            var mergedSeen = new Dictionary<CompProperties_Facility, HashSet<ThingDef>>();

            // 先建一张「组件配置 → 它属于哪个 Def」的对照表。
            // 用途见下面写回那一遍：要把「自己」从自己的名单里剔掉，就得知道这个 props 是谁身上的。
            // 注意好几个 Def 可能共用同一个 props 实例，所以遇到已记过的就跳过 ——
            // 反正自连判定关心的是「名单里那项是不是它自己」，取哪一个都一样。
            var propsOwner = new Dictionary<CompProperties_Facility, ThingDef>();
            for (int i = 0; i < facilityRecords.Count; i++)
            {
                FacilityRecord rec = facilityRecords[i];
                if (rec?.props != null && rec.def != null && !propsOwner.ContainsKey(rec.props))
                {
                    propsOwner[rec.props] = rec.def;
                }
            }

            for (int i = 0; i < facilityRecords.Count; i++)
            {
                FacilityRecord rec = facilityRecords[i];
                if (rec?.props == null || rec.def == null)
                {
                    continue;
                }

                if (!merged.TryGetValue(rec.props, out List<ThingDef> target))
                {
                    target = new List<ThingDef>();
                    merged[rec.props] = target;
                    mergedSeen[rec.props] = new HashSet<ThingDef>();
                }
                HashSet<ThingDef> seen = mergedSeen[rec.props];

                if (table.TryGetValue(rec.def, out List<ThingDef> rebuilt))
                {
                    for (int j = 0; j < rebuilt.Count; j++)
                    {
                        if (seen.Add(rebuilt[j]))
                        {
                            target.Add(rebuilt[j]);
                        }
                    }
                }

                // 并集合并：把基线里本来就有、但上面这套重建算法算不出来的条目补回去。
                //（别的模组用自定义子类手工塞进去的条目就属于这种，绝不能被我们抹掉。）
                if (rec.originalBuildings != null)
                {
                    for (int j = 0; j < rec.originalBuildings.Count; j++)
                    {
                        ThingDef b = rec.originalBuildings[j];
                        if (b != null && seen.Add(b))
                        {
                            target.Add(b);
                        }
                    }
                }
            }

            // 最后把合并好的结果逐个写回组件。写之前先比一比：
            // 内容真的变了才把「改动过」这个标记立起来。
            foreach (KeyValuePair<CompProperties_Facility, List<ThingDef>> pair in merged)
            {
                // 先剔除「自己连自己」，**必须放在下面的比较之前**。
                //
                // 为什么这件事要做：同一个 Def 同时挂了床和设施两个组件时，
                // 它不该出现在自己的名单里（否则会凭空吃一份自己给的加成）。
                // 注入侧（InjectIntoLinkingSide）与还原侧（OnFacilityResolveReferences）
                // 都已经跳过了自连，这里补的是第三条路径 —— 万一某个 Def 的**基线本来就
                // 带着自连**，重建时会把它原样写回去。
                // propsOwner 的作用就是反查「这个组件配置属于哪个 Def」。
                //
                // 为什么顺序不能反：「有没有改动过」是拿剔除**之后**的名单跟当前名单比的。
                // 要是把剔除放到比较后面，那么「唯一的变化就是自连被剔掉」这种情况
                // 就不会立起 LinkTableChanged —— 地图上早就摆好的那件设施也就不会被重链，
                // 它会继续吃自己给的那份加成，直到玩家重新进一次地图为止。
                if (propsOwner.TryGetValue(pair.Key, out ThingDef selfDef))
                {
                    pair.Value.RemoveAll(delegate (ThingDef t) { return ReferenceEquals(t, selfDef); });
                }

                if (!SameSequence(pair.Key.linkableBuildings, pair.Value))
                {
                    LinkTableChanged = true;
                }

                pair.Key.linkableBuildings = pair.Value;
                rebuiltFacilityCount++;
            }

            // 上面这一遍只覆盖「本次仍然算床用设施」的那些；退出名单的要单独收拾。
            RestoreRetiredFacilities();
        }

        /// <summary>
        /// 把「以前被本模组扩充过链接表、这次却不在名单里」的设施，按基线还原回去。
        /// 这是上面那个写回循环只遍历 facilityRecords 的必要补充。
        ///
        /// 为什么少了它不行：设施一旦被策略排除（excludedFacilities），或者因为玩家
        /// 关掉了激进模式而不再被判定为床用设施，它就不会出现在 facilityRecords 里，
        /// 上面那个循环也就不会碰它 —— 它的 linkableBuildings 会一直保留着上一次
        /// 「含全部床」的陈旧值。而原版判断一个设施能否连到某张床时，只看设施这一侧
        /// （CompAffectedByFacilities.CanLinkTo 并不检查床那边的名单），于是床照样
        /// 吃到它的加成，玩家在策略里排除它却看不到任何效果。
        ///
        /// 写法和 ApplyLinkLimitOverride 一致：遍历「所有记录过基线的设施」而不是
        /// 本次判定的那一批，这样才盖得住已经退场的；而且只有内容真的和基线不一样
        /// 才动手、才置位 LinkTableChanged，免得平白多一次全地图重链。
        /// </summary>
        private static void RestoreRetiredFacilities()
        {
            if (facilityBaseline.Count == 0)
            {
                // 从来没记录过任何基线，也就没有历史包袱，直接收工。
                return;
            }

            // 先把本次真正处理到的设施 Def 收进集合，后面用来判断谁退场了。
            var handled = new HashSet<ThingDef>();
            for (int i = 0; i < facilityRecords.Count; i++)
            {
                ThingDef facDef = facilityRecords[i]?.def;
                if (facDef != null)
                {
                    handled.Add(facDef);
                }
            }

            foreach (KeyValuePair<ThingDef, List<ThingDef>> pair in facilityBaseline)
            {
                if (pair.Key == null || handled.Contains(pair.Key))
                {
                    // 这次依然算数，上面那一遍已经处理过它了。
                    continue;
                }

                // 按 Def 现取组件，而不是从 facilityRecords 里拿 ——
                // 要还原的恰恰是那些已经不在 facilityRecords 里的。
                CompProperties_Facility props = pair.Key.GetCompProperties<CompProperties_Facility>();
                if (props?.linkableBuildings == null)
                {
                    // 组件或名单已经不在了，没什么可还原的。
                    continue;
                }

                // pair.Value 可能是 null（原版本来就没有这个名单，见 facilityBaseline 的存法）：
                // 同样不能拿 null 去比 —— SameSequence 见到 null 一律返回 false，
                // 「基线还没建立（null）」会被当成「和现在不一样」，每次重扫都白触发一次全地图重链。
                // 归一化成空表之后，null 基线与空名单才会被正确判成「没变化」。
                if (SameSequence(props.linkableBuildings, pair.Value ?? EmptyDefList))
                {
                    // 已经和基线一模一样，不用动它，也不必惊动重链。
                    continue;
                }

                // 原地清空再重填，保持这个 List 对象本身不变 ——
                // 别的代码可能正握着这个名单的引用。
                props.linkableBuildings.Clear();
                List<ThingDef> baseLine = pair.Value;
                if (baseLine != null)
                {
                    for (int j = 0; j < baseLine.Count; j++)
                    {
                        if (baseLine[j] != null)
                        {
                            props.linkableBuildings.Add(baseLine[j]);
                        }
                    }
                }

                LinkTableChanged = true;
            }
        }

        /// <summary>
        /// 一个位置一个位置地比，看两个列表是不是一模一样（前后顺序也必须相同）。
        /// 用来判断链接名单这次到底有没有真的发生变化 —— 没变就不必惊动任何人去刷新。
        /// </summary>
        private static bool SameSequence(List<ThingDef> a, List<ThingDef> b)
        {
            if (ReferenceEquals(a, b))
            {
                // 根本就是同一个对象，那当然一样，连比都不用比了。
                return true;
            }
            if (a == null || b == null || a.Count != b.Count)
            {
                // 有一个是空的、或者长度都对不上，那肯定不一样。
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    // 只要有一个位置对不上，就算不同。
                    return false;
                }
            }
            return true;
        }

        // ───────────────────────── 阶段 5：链接上限 ─────────────────────────

        /// <summary>
        /// 第 5 步（可选）：放开链接数量上限，只作用于判定出来的床用设施。
        /// 遍历的是「所有记录过基线的设施」，而不是这次判定出来的那一批 ——
        /// 这样即使玩家同时关掉了激进模式、让某些设施不再算数，
        /// 它们之前被放大过的 maxSimultaneous 也能准确还原回去。
        /// </summary>
        private static void ApplyLinkLimitOverride()
        {
            bool enabled = Settings?.ignoreLinkLimit ?? false;

            foreach (KeyValuePair<ThingDef, int> pair in maxSimultaneousBaseline)
            {
                // 注意这里是「按 Def 现取组件」，不是从 facilityRecords 里拿 ——
                // 因为要覆盖的，恰恰可能是那些已经不算床用设施的 Def。
                CompProperties_Facility props = pair.Key.GetCompProperties<CompProperties_Facility>();
                if (props == null)
                {
                    // 组件没了（比如被别的模组中途改过），跳过。
                    continue;
                }

                // 用 Max 是为了尊重「作者本来就给了更大上限」的情况：我们只放大、不缩小。
                int wanted = enabled && bedFacilitySet.Contains(pair.Key)
                    ? Math.Max(pair.Value, LinkLimitOverrideValue)
                    : pair.Value;

                if (props.maxSimultaneous != wanted)
                {
                    props.maxSimultaneous = wanted;
                    LinkTableChanged = true;
                }
            }
        }

        // ───────────────────── 阶段 6：过小的链接距离 ─────────────────────

        /// <summary>
        /// 第 6 步（可选）：修正那些「最大链接距离比一个格子还小」的床用设施。
        ///
        /// 为什么需要修：游戏里格子与格子之间的最小间距就是 1。要是作者把 maxDistance
        /// 写成了 0.9 这种小于 1 的数，那在机制上就等价于「它必须和床挤在同一格里」——
        /// 摆到隔壁格时，两格中心的水平距离就已经是 1.0 了，距离判定必然超标，
        /// 于是这件家具永远连不上任何一张床。
        /// GloomyFurniture 的泰迪熊（GL_Teddy，maxDistance=0.9）正是这种情况，
        /// 而它的作者显然是想让它给床加舒适度的（它自家床的名单里就写着这只熊）。
        ///
        /// 只处理数值落在 (0, 1) 开区间里的设施：0 表示「我谁都不连」，
        /// 大于等于 1 的数值都是作者的有效意图，这两种一律不碰。
        /// 和链接上限一样，遍历的是基线全表，所以关掉开关之后能准确还原。
        /// </summary>
        private static void ApplyMaxDistanceFix()
        {
            // 兜底值用 true，跟 CompatSettings 里那个字段的默认值保持一致 ——
            // 免得「设置对象没建起来」这种罕见情况下本修正悄悄失效，
            // 而界面上显示的却还是「默认开启」，对不上。
            bool enabled = Settings?.fixTooSmallMaxDistance ?? true;

            foreach (KeyValuePair<ThingDef, float> pair in maxDistanceBaseline)
            {
                CompProperties_Facility props = pair.Key.GetCompProperties<CompProperties_Facility>();
                if (props == null)
                {
                    continue;
                }

                float baseline = pair.Value;
                bool tooSmall = baseline > 0f && baseline < 1f;

                // 只有「距离判定真的会执行到」的设施，才值得去改它的 maxDistance。
                //
                // 原版 CompAffectedByFacilities.CanPotentiallyLinkTo_Static 里的判断顺序是：
                //   if (mustBePlacedAdjacent) {...}
                //   if (mustBePlacedFacingThingLinear) { ...; return false/true; }   <-- 一定会提前返回
                //   if (mustBePlacedAdjacentCardinalToBedHead || ...AndFacingBedHead) {...}
                //   if (!mustBePlacedAdjacent && !...CardinalToBedHead && !...AndFacingBedHead)
                //       { 走到这里才会做 Vector3.Distance 与 maxDistance / minDistance 的比较 }
                //
                // 也就是说：最后那段距离判定的守卫只列了三个标志，可是
                // mustBePlacedFacingThingLinear（必须正对着）那个分支排在它前面，
                // 一旦命中就已经 return 了，永远走不到距离检查。
                // 给这类设施改 maxDistance 属于白改，所以这里把它们一并排除，
                // 免得日志里冒出「已修正距离」的假记录。
                bool usesMaxDistance = !props.mustBePlacedAdjacent
                                       && !props.mustBePlacedAdjacentCardinalToBedHead
                                       && !props.mustBePlacedAdjacentCardinalToAndFacingBedHead
                                       && !props.mustBePlacedFacingThingLinear;

                // 还得保证改完之后「有效距离区间」不是空的。原版的判定写法是
                //   num > maxDistance || (minDistance > 0f && num < minDistance)
                // 注意后半句带着 minDistance > 0f 这个前提：minDistance 是 0 或负数时，
                // 原版压根不检查下界。可要是 minDistance 已经不小于我们想改成的目标值，
                // 那把 maxDistance 抬高只会得到一个空区间（上界比下界还小），照样连不上。
                bool rangeStaysValid = props.minDistance < ReasonableMaxDistance;

                float wanted = enabled && tooSmall && usesMaxDistance && rangeStaysValid
                               && bedFacilitySet.Contains(pair.Key)
                    ? ReasonableMaxDistance
                    : baseline;

                if (props.maxDistance != wanted)
                {
                    props.maxDistance = wanted;
                    LinkTableChanged = true;

                    // 只有「真的往上抬了」才打这条日志。
                    //
                    // 为什么要区分：关掉开关、或者该设施不再是床用设施时，wanted 会回到
                    // baseline；只要 baseline 本来就落在 (0,1) 区间，上面的 != 判断同样成立。
                    // 那时若照打不误，文案会变成「原本是 0.9，已抬到 0.9」，自相矛盾。
                    if (tooSmall && wanted == ReasonableMaxDistance
                        && (Settings?.verboseLogging ?? false))
                    {
                        Log.Message("[UBFC] " + "UBFC_Log_MaxDistanceFixed".Translate(
                            pair.Key.defName, baseline, wanted));
                    }
                }
            }
        }

        // ───────────────────────── 对外服务 ─────────────────────────

        /// <summary>
        /// Harmony 保险丝。万一在游戏运行期间有别人又调了一次 ResolveReferences
        /// （比如别的模组做动态 Def 处理、开发者工具、热重载……），
        /// 推导结果就会退回成「只覆盖原版床」，甚至把床这边的名单也重置回 XML 里写的原值。
        /// 这里把<b>两边</b>都补齐回去：
        ///   · 设施那边 linkableBuildings —— 保证设施仍然能主动找到全部床；
        ///   · 床那边 linkableFacilities —— 保证床这边的发现逻辑、以及放置时的预览连线不走样。
        /// 这是全模组唯一一处 Harmony 补丁，而且只挂在 Def 加载这条冷路径上
        /// （也就是只在游戏启动、解析 Def 数据时会被调用；平时的游戏循环、画面渲染都不会走到它，
        /// 所以对帧率没有任何影响）。
        /// </summary>
        internal static void OnFacilityResolveReferences(CompProperties_Facility props, ThingDef parentDef)
        {
            // 这层 try/catch 是 2026-10-04 审计后补上的，必须有：
            //
            // 本方法是挂在 ThingDef.ResolveReferences 内部那个 comps 循环上的 postfix。
            // 一旦把异常抛出去，**同一个 ThingDef 上排在后面的其它 CompProperties
            // 就再也不会被 ResolveReferences** —— 而那些是别的模组的初始化代码。
            // 也就是说：本模组自己出的小事，会变成别人的大事。
            //
            // 保险丝本来就是「锦上添花」，失败只该记一条日志、安静跳过。
            // 用 ErrorOnce 而不是 Error：它会被每一件床用设施各调用一次，普通 Error 会刷屏。
            try
            {
                OnFacilityResolveReferencesCore(props, parentDef);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[UBFC] " + "UBFC_Log_FuseFailed".Translate() + "\n" + ex, FuseFailedErrorKey);
            }
        }

        /// <summary>
        /// 保险丝的实际逻辑。外面那层 <see cref="OnFacilityResolveReferences"/> 只负责
        /// 兜住异常，本方法体与它原本的内容完全一致。
        /// </summary>
        private static void OnFacilityResolveReferencesCore(CompProperties_Facility props, ThingDef parentDef)
        {
            if (!Ready || props == null || parentDef == null || !bedFacilitySet.Contains(parentDef))
            {
                // 引擎还没就绪、参数不合法、或者这件设施压根不在我们的床用设施名单里，
                // 那就什么都别做，直接返回 —— 保险丝只保我们管得着的东西。
                return;
            }

            if (props.linkableBuildings == null)
            {
                // 名单是空的就先建一张空表，下面好往里补。
                props.linkableBuildings = new List<ThingDef>();
            }

            // 下面这一段用 HashSet 判断「这一项是不是已经在名单里」，而不是
            // 每次都去 List.Contains。
            //
            // 为什么要这么改（2026-10-04 审计）：这一段是嵌套的 ——
            // 外层遍历全部床 Def，内层每次都要在这份不断变长的名单里找一遍，
            // 所以是 O(床Def数²)；而整个方法**每一件床用设施都会各调用一次**。
            //
            // 需要说清楚它**什么时候才会真的跑**（这一点早先的注释写错了、已更正）：
            // 本方法是挂在 ThingDef.ResolveReferences 上的保险丝，而那个阶段
            // 早于 StaticConstructorOnStartupUtility.CallAll，此时 Ready 还是 false，
            // 所以**正常启动路径上它一次都不会执行**（首行就早退了）。
            // 真正会跑到这里的，是「运行期热重载 Def」（开发者工具）那类场景。
            // 也就是说：这是热重载时的一次性开销，**不是启动开销**，
            // 早先写成「启动卡十几秒」是夸大，实际启动根本走不到这里。
            //
            // 之所以仍然要改：床 Def 上千的重度整合包会把它放大到十几亿次比较。
            // 换成 HashSet 之后内层查找变成 O(1)，**语义完全不变**：
            // ThingDef 没有实现 IEquatable<ThingDef>，所以
            // HashSet 与 List.Contains 走的都是引用比较（已用反编译核实），
            // 补入顺序也仍按 bedDefs 的原有次序。
            fuseScratch.Clear();
            for (int i = 0; i < props.linkableBuildings.Count; i++)
            {
                ThingDef already = props.linkableBuildings[i];
                if (already != null)
                {
                    fuseScratch.Add(already);
                }
            }

            int added = 0;
            for (int i = 0; i < bedDefs.Count; i++)
            {
                ThingDef bed = bedDefs[i];
                // 与常规注入路径（InjectIntoLinkingSide）保持同一条规则：跳过「它自己」。
                // 有些 Def 同时挂了床和设施两个组件，若把 parentDef 也写进它自己的
                // linkableBuildings，这张床就会凭空多出一份自己提供的加成。
                if (bed == null || ReferenceEquals(bed, parentDef))
                {
                    continue;
                }
                // HashSet.Add 返回 false 就表示「原本就在里面」，
                // 正好顶上原来 AddIfMissing 内部那次 Contains。
                if (fuseScratch.Add(bed))
                {
                    props.linkableBuildings.Add(bed);
                    // 补进去一张，就记一笔，最后用来判断「这次到底有没有真的动过东西」。
                    added++;
                }
            }

            // 顺带把床那边的名单也补一下。因为 Def 热重载（开发者工具）会把
            // linkableFacilities 重置回 XML 里的原值，而床这边的 PotentialThingsToLinkTo
            // 正是靠它来主动发现设施的。
            // 设施那边已经能独立完成链接，所以就算不补，功能也不会丢；
            // 补上它才能让两边的状态保持一致，也让「放置时的预览连线」这类
            // 依赖床侧名单的显示不至于失真。
            int restored = 0;
            for (int i = 0; i < bedDefs.Count; i++)
            {
                ThingDef bed = bedDefs[i];
                // 同样跳过「它自己」：parentDef 若既是设施又是床，不该把自己连到自己名下。
                if (bed == null || ReferenceEquals(bed, parentDef))
                {
                    continue;
                }
                CompProperties_AffectedByFacilities affected =
                    bed.GetCompProperties<CompProperties_AffectedByFacilities>();
                List<ThingDef> bedList = affected?.linkableFacilities;
                if (bedList == null)
                {
                    continue;
                }

                // 这里**故意不用** fuseScratch，改成直接线性扫一遍。
                //
                // 理由（2026-10-04 复审）：建一个哈希集和扫一遍列表**都是 O(名单长度)**，
                // 成本一模一样；但线性扫描不碰那个共享的静态集合，
                // 就少了一份「谁在什么时候把它 Clear 掉了」的隐患。
                // 床侧名单的长度 = 这台设施原本登记过的床数，通常很小，扫一遍根本不值一提。
                bool alreadyLinked = false;
                for (int k = 0; k < bedList.Count; k++)
                {
                    if (ReferenceEquals(bedList[k], parentDef))
                    {
                        alreadyLinked = true;
                        break;
                    }
                }
                if (!alreadyLinked)
                {
                    bedList.Add(parentDef);
                    restored++;
                }
            }

            if (added > 0 || restored > 0)
            {
                // 只要真的补过东西，就说明数据被别人改过、我们又修回来了，
                // 该立起「变动过」的标记，让重链知道地图上的家具需要跟着刷新。
                LinkTableChanged = true;
                if (Settings?.verboseLogging ?? false)
                {
                    Log.Message("[UBFC] " + "UBFC_Log_FuseApplied".Translate(parentDef.defName, added + restored));
                }
            }
        }

        /// <summary>
        /// 启动之后的一次性完整性自检，由 <see cref="CompatGameComponent"/> 在第一个 tick 调用。
        ///
        /// 为什么需要它：本模组的扫描挂在 [StaticConstructorOnStartup] 上，而
        /// StaticConstructorOnStartupUtility.CallAll() 是严格按**模组加载顺序**
        /// 一个一个调用静态构造函数的。只要有一个排在本模组后面的模组，
        /// 在自己的静态构造里把 CompProperties_Facility.linkableBuildings 整个重建了一遍
        /// （他们想把自家新家具接到自家新床上时就会这么写），
        /// 本模组写下的注入就会被无声无息地抹掉。
        /// Harmony 保险丝只能覆盖「ResolveReferences 又被调用了一次」这一条路径，
        /// 覆盖不了「直接给字段换一张新列表」这种做法。
        ///
        /// 检查本身是纯只读的：床侧与设施侧各扫一遍，而每比对一张名单都要先把对面的
        /// 完整名单倒进临时哈希集（见 ContainsAll），所以总开销约 2×(床数 × 设施数) 次哈希查找，
        /// 另加每次重建临时集合的「名单长度之和」次插入。按 200 床 × 300 设施估算约十几万次
        /// 哈希操作，且只在载入存档后的第一个 tick 跑一次，这个量级是可以接受的；
        /// 只有真的需要修的时候，才会重走一遍注入和重建 ——
        /// 那两个方法都可重复调用、且都是从基线重建的，所以再跑一次也不会出错。
        /// </summary>
        /// <returns>true 表示发现数据被外部改写了，并且已经修好。</returns>
        public static bool EnsureLinkTablesIntact()
        {
            if (!Ready || bedDefs.Count == 0 || bedFacilityDefs.Count == 0)
            {
                // 引擎还没就绪，或者压根没有床 / 没有设施，
                // 那就谈不上「完整不完整」，直接回报「没事」。
                return false;
            }

            if (BedsStillLinkToAllFacilities() && FacilitiesStillLinkToAllBeds())
            {
                // 两边都完好无损，就不用修，也不立「变动过」的标记。
                return false;
            }

            // 这里没办法在不增加一大堆状态记录的前提下说清「到底是哪个模组改的」，
            // 所以只报一句警告；玩家打开详细日志后手动重扫一次，就能看到完整清单。
            // 同样只报一次：每次 Initialize（含连点重扫）都会走到这里，
            // 若链接表被持续篡改，不去重就会连着刷。
            if (!tablesTamperedLogged)
            {
                tablesTamperedLogged = true;
                Log.Warning("[UBFC] " + "UBFC_Log_TablesTampered".Translate());
            }

            // 下面的重做会把注入数和重建数重新累加一遍，所以先把上一次的计数清零，
            // 免得设置界面的报告里出现双倍的数字。床数和设施数本方法不会改动，保持原样即可。
            injectedLinkCount = 0;
            rebuiltFacilityCount = 0;

            // 按顺序把相关步骤重做一遍，把被抹掉的东西补回来。
            InjectIntoLinkingSide();
            RebuildFacilityLinkTables();
            ApplyLinkLimitOverride();
            ApplyMaxDistanceFix();
            return true;
        }

        /// <summary>床这一侧的检查：每一张床的名单里，是不是都还留着全部床用设施。</summary>
        private static bool BedsStillLinkToAllFacilities()
        {
            for (int i = 0; i < bedDefs.Count; i++)
            {
                CompProperties_AffectedByFacilities affected =
                    bedDefs[i].GetCompProperties<CompProperties_AffectedByFacilities>();
                List<ThingDef> list = affected?.linkableFacilities;
                if (list == null)
                {
                    // 名单被整个抹成 null 了（比如热重载重置回 XML 原值），
                    // 那显然是被动过，直接判定「不完整」。
                    return false;
                }

                // 第三个参数是「这张床自己」：万一它同时也是床用设施，
                // 注入侧会把「自己连自己」那条切掉，自检也得同样放过它，
                // 否则这里永远判定「名单不完整」。
                if (!ContainsAll(list, bedFacilityDefs, bedDefs[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 设施这一侧的检查：每一件床用设施的 linkableBuildings 里，是不是都还留着全部床。
        /// 这里直接按 Def 去取 CompProperties，没有绕道 facilityRecords ——
        /// 遇上多个 Def 共用同一个 CompProperties 实例时，无非就重复检查几次，
        /// 换来的是实现更短、更直白。
        /// </summary>
        private static bool FacilitiesStillLinkToAllBeds()
        {
            for (int i = 0; i < bedFacilityDefs.Count; i++)
            {
                CompProperties_Facility props = bedFacilityDefs[i].GetCompProperties<CompProperties_Facility>();
                List<ThingDef> list = props?.linkableBuildings;
                if (list == null)
                {
                    return false;
                }

                // 同上：这件设施自己也可能同时是床，自连那一条是被我们主动切掉的，
                // 所以自检要放过它。
                if (!ContainsAll(list, bedDefs, bedFacilityDefs[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 检查 needles 里的每一样东西是不是都在 haystack 里。
        /// 先把 haystack 装进一个静态临时集合，这样内层的查询就从「挨个找」变成了「一次查到」。
        /// </summary>
        /// <param name="haystack">要检查的那份名单（某件设施的 linkableBuildings，或某张床的 linkableFacilities）。</param>
        /// <param name="needles">名单里**应该**有的全部对象。</param>
        /// <param name="self">
        /// 「既是床、又是床用设施」的那个 Def（没有就传 null）。
        ///
        /// 为什么必须把它传进来（2026-10-04 复审查出的逻辑缺陷）：
        /// 本模组在注入侧与重建侧**主动切断**了「自己连自己」那一条
        ///（见 InjectIntoLinkingSide 与 RebuildFacilityLinkTables 里的 ReferenceEquals 判断），
        /// 理由是原版规则会让这种 Def 给自己凭空加一份属性。
        /// 但下面这个自检原本是「一种设施都不能少」—— 那个被我们**故意**摘掉的自己
        /// 当然永远查不到，于是必然返回 false，完整性自检就永远认定「名单不完整」。
        ///
        /// 后果不是功能坏掉，而是每次读档都白跑一次全量重注入 + 全地图重链，
        /// 还会打出一条 UBFC_Log_TablesTampered —— 把本模组自己的设计
        /// 说成「别的模组把链接表重建了」，把排查的人往完全错误的方向带。
        /// 所以这里显式跳过 self：只对它一个人网开一面，别的一概照旧。
        /// </param>
        private static bool ContainsAll(List<ThingDef> haystack, List<ThingDef> needles, ThingDef self)
        {
            // 下面这段是「把 haystack 倒进临时集合」，用完即清，
            // 所以这个静态集合可以反复借来用，不会串味。
            intactCheckScratch.Clear();
            for (int i = 0; i < haystack.Count; i++)
            {
                if (haystack[i] != null)
                {
                    intactCheckScratch.Add(haystack[i]);
                }
            }

            for (int i = 0; i < needles.Count; i++)
            {
                // 唯独放过「它自己」：那一条是本模组主动切断的，不是丢失。
                if (self != null && ReferenceEquals(needles[i], self))
                {
                    continue;
                }

                if (!intactCheckScratch.Contains(needles[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 如果此刻正处在「游戏进行中」的状态，就把所有已加载的地图各重新链接一次。
        /// 这条路径是给「玩家在设置界面手动点重新扫描」用的 —— 让改动立刻见效，
        /// 不必退出地图再重进。游戏启动阶段 ProgramState 还不是 Playing，
        /// 所以这里不会和 CompatGameComponent 在第一个 tick 做的重链重复劳动。
        /// </summary>
        private static void RelinkAllMapsIfPlaying()
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                // 还没进游戏（比如刚启动、还停在主菜单），没有地图可刷，收工。
                return;
            }

            List<Map> maps = Find.Maps;
            if (maps == null || maps.Count == 0)
            {
                return;
            }

            // 计个时，详细日志里会报告「刷完所有地图花了多少毫秒」。
            var watch = Stopwatch.StartNew();

            // 每张地图单独包一层 try/catch（2026-10-04 审计补上）。
            //
            // 为什么不直接写一个 for 循环：玩家可能同时有多张地图
            //（主地图 + 商队/飞船地图 + 已生成的定居点）。第 0 张地图上只要有一件设施
            // 抛异常（比如别的模组覆写过 CanLinkTo 的建筑），整个循环就会中断 ——
            // 其余地图上的家具保持旧状态，而玩家看到的现象是「点了重新扫描没反应」。
            // CompatGameComponent 里进游戏时那次重链本来就是每张图各包一层，这里与它对齐。
            int relinkedMaps = 0;
            for (int i = 0; i < maps.Count; i++)
            {
                try
                {
                    RelinkMap(maps[i]);
                    relinkedMaps++;
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[UBFC] " + "UBFC_Log_RelinkFailed".Translate() + " (map index " + i + ")\n" + ex,
                        RelinkMapErrorKey);
                }
            }
            watch.Stop();

            if (Settings?.verboseLogging ?? false)
            {
                // 报「成功重链了几张」而不是「总共有几张」—— 中途失败时数字才说实话。
                Log.Message("[UBFC] " + "UBFC_Log_RelinkAllDone".Translate(relinkedMaps, watch.ElapsedMilliseconds));
            }
        }

        /// <summary>
        /// 对地图上已经摆着的设施做一次重新链接。目的是让老存档里早就建好的家具立刻生效，
        /// 同时把「上次算床用设施、这次不算」的设施身上残留的链接解开。
        ///
        /// 只重链设施这一侧就够了：CompFacility.LinkToNearbyBuildings() 会先把自己身上的
        /// 旧链接全部断开（UnlinkAll），再为每一个候选床建立双向链接；
        /// 而且它不像被影响方那样要用 LINQ 排序，开销更低。
        /// 这也和原版 PostMapInit（地图初始化完成时）的做法一致 —— 原版同样只重链设施这一侧。
        /// </summary>
        public static void RelinkMap(Map map)
        {
            if (!Ready || map?.listerThings == null || everInjectedFacilities.Count == 0)
            {
                // 引擎没就绪、地图还没准备好、或者本模组压根没动过任何设施，都不用忙活。
                return;
            }

            int touched = 0;
            foreach (ThingDef def in everInjectedFacilities)
            {
                // 通过 listerThings 按 Def 直接取这一类东西的实例清单，
                // 避免把整张地图上的所有 Thing 从头到尾翻一遍。
                List<Thing> things = map.listerThings.ThingsOfDef(def);
                if (things == null || things.Count == 0)
                {
                    // 这张地图上没摆过这件家具，换下一件。
                    continue;
                }

                for (int j = 0; j < things.Count; j++)
                {
                    Thing thing = things[j];
                    if (thing == null || !thing.Spawned || thing.Map != map)
                    {
                        // 已经被拆掉 / 还没放下去 / 根本不属于这张地图的，跳过。
                        continue;
                    }

                    CompFacility comp = thing.TryGetComp<CompFacility>();
                    if (comp == null)
                    {
                        // 它身上没有设施组件，那不归我们管。
                        continue;
                    }

                    // Notify_ThingChanged() 是原版公开的入口，内部做的事等价于 RelinkAll()，
                    // 也就是「断开旧链接、重新连一遍」。
                    comp.Notify_ThingChanged();
                    touched++;
                }
            }

            if (touched > 0 && (Settings?.verboseLogging ?? false))
            {
                Log.Message("[UBFC] " + "UBFC_Log_RelinkDone".Translate(map.uniqueID, touched));
            }
        }

        /// <summary>
        /// 拼出一段给人看的扫描报告，供设置界面显示。
        /// 结果会被缓存下来，免得设置窗口每帧都重新拼一遍字符串
        /// （设置窗口是每帧都会重画的，不缓存就等于白白烧 CPU）。
        /// </summary>
        public static string BuildReport()
        {
            bool verbose = Settings?.verboseLogging ?? false;
            if (reportCache == null || reportCacheVerbose != verbose)
            {
                // 没缓存，或者玩家中途切换了详细日志开关（那样报告内容就不一样了），
                // 才重新拼一次。
                reportCache = BuildReportInternal(verbose);
                reportCacheVerbose = verbose;
            }
            return reportCache;
        }

        /// <summary>
        /// 真正动手拼报告文本的地方，每次调用都会重新构造字符串。
        /// 缓存、以及「什么时候该作废重拼」的判断都不在这里，而在 <see cref="BuildReport"/> 里。
        /// </summary>
        private static string BuildReportInternal(bool verbose)
        {
            if (!Ready)
            {
                // 引擎还没跑起来，报告里如实写一句「尚未就绪」就好。
                return "UBFC_Report_NotReady".Translate().ToString();
            }

            var lines = new List<string>
            {
                "UBFC_Report_Beds".Translate(bedDefs.Count).ToString(),
                "UBFC_Report_Facilities".Translate(bedFacilityDefs.Count).ToString(),
                "UBFC_Report_Injected".Translate(injectedLinkCount).ToString(),
                "UBFC_Report_Rebuilt".Translate(rebuiltFacilityCount).ToString()
            };

            // 下面两类都是少见情况：没有就不提，免得报告里全是 0。
            if (absorbedPseudoBedCount > 0)
            {
                lines.Add("UBFC_Report_ExtraBeds".Translate(absorbedPseudoBedCount).ToString());
            }

            if (bedsWithoutCompCount > 0)
            {
                lines.Add("UBFC_Report_BedsWithoutComp".Translate(bedsWithoutCompCount).ToString());
            }

            if (verbose)
            {
                // 开了详细日志才列清单，而且只列前 ReportSampleLimit 个（当前 8），免得报告长到看不完。
                // 不写死数字：那个常量以后调了，这里也不会跟着过期。
                lines.Add("UBFC_Report_FacilityList".Translate(SampleDefNames(bedFacilityDefs, ReportSampleLimit)).ToString());
                lines.Add("UBFC_Report_BedList".Translate(SampleDefNames(bedDefs, ReportSampleLimit)).ToString());
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 取前若干个 defName 拼成一行显示，多出来的部分折叠成「…(+N)」，
        /// 免得报告长得没边。
        ///
        /// 拼好之后还会按 <see cref="ReportLineWrapWidth"/> 折行 ——
        /// 因为设置面板的 Label 不会自动换行，不折的话整块面板会被撑破。
        /// </summary>
        private static string SampleDefNames(List<ThingDef> defs, int max)
        {
            var result = new List<string>();
            if (defs == null)
            {
                return string.Empty;
            }

            int count = Math.Min(defs.Count, max);
            for (int i = 0; i < count; i++)
            {
                result.Add(defs[i].defName);
            }
            if (defs.Count > count)
            {
                // 被截掉的部分用「…(+还剩多少)」提示一下，让玩家知道后面还有。
                result.Add("…(+" + (defs.Count - count) + ")");
            }
            return WrapToWidth(string.Join("、", result), ReportLineWrapWidth);
        }

        /// <summary>
        /// 把一段长文本按固定字符数折成多行。
        ///
        /// 为什么需要它：设置面板的文字是用 Widgets.Label 画的，而它<b>不会自动换行</b>，
        /// 一行几百个字符会把整个面板的布局撑破，玩家反而看不到下面的开关和按钮。
        /// 折行纯粹是为了好看，不改动任何数据。
        /// </summary>
        /// <param name="text">原始文本。</param>
        /// <param name="width">每行最多放几个字符。</param>
        private static string WrapToWidth(string text, int width)
        {
            if (string.IsNullOrEmpty(text) || width <= 0 || text.Length <= width)
            {
                // 短文本直接原样返回，不做无谓的搬运。
                return text;
            }

            var sb = new System.Text.StringBuilder(text.Length + text.Length / width + 1);
            for (int i = 0; i < text.Length; i += width)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(text, i, Math.Min(width, text.Length - i));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把这次扫描的统计数字写进日志。
        /// 伪床和「缺少组件的床」都算少见情况，但处理方式不同：
        /// 前者只在详细日志下输出；后者因为意味着某些床永远吃不到设施加成、
        /// 值得让玩家知道，所以无论开不开详细日志都会输出。
        /// </summary>
        private static void LogSummary()
        {
            // 这条是「本次扫描的总结」，永远输出，让玩家一眼看到扫描结果。
            Log.Message("[UBFC] " + "UBFC_Log_ScanDone".Translate(
                bedDefs.Count, bedFacilityDefs.Count, injectedLinkCount, rebuiltFacilityCount));

            // 伪床属于少见情况，只在开了详细日志时才刷屏打扰玩家
            if (absorbedPseudoBedCount > 0 && (Settings?.verboseLogging ?? false))
            {
                Log.Message("[UBFC] " + "UBFC_Log_ExtraBeds".Translate(absorbedPseudoBedCount));
            }

            if (bedsWithoutCompCount > 0)
            {
                Log.Message("[UBFC] " + "UBFC_Log_BedsWithoutComp".Translate(bedsWithoutCompCount));
            }

            // 「床自己也是床用设施」同样属于少见情况，只在详细日志里提一句
            if (overlappingDefCount > 0 && (Settings?.verboseLogging ?? false))
            {
                Log.Message("[UBFC] " + "UBFC_Log_OverlappingDefs".Translate(overlappingDefCount));
            }
        }

    }
}
