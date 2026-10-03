using System.Collections.Generic;
using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 游戏组件：专门负责两件「只有进了游戏之后才做得了」的收尾工作。
    /// （GameComponent 是 RimWorld 提供的一种挂件，游戏跑起来之后每个 tick 都会被叫一次。）
    ///
    /// 1. 完整性自检（每局只做一次）
    ///    Def 数据是 CompatBootstrap 在 [StaticConstructorOnStartup] 里改好的，
    ///    而静态构造函数是按模组加载顺序挨个调用的 —— 排在本模组后面的模组，
    ///    如果也把同一批名单整个重建了一遍，本模组刚写进去的东西就会被覆盖掉。
    ///    所以这里在游戏跑起来的第一个 tick 检查一次，发现问题当场修好，
    ///    详见 <see cref="CompatEngine.EnsureLinkTablesIntact"/>。
    ///
    /// 2. 重新链接地图上已有的家具（每次载入存档后做一次）
    ///    链接关系不写在存档里，而是建筑被放下去（SpawnSetup / PostMapInit）时当场建立的。
    ///    正常情况下载入存档会自动重建一遍，但如果是玩家在游戏中途改了设置，
    ///    或者某些模组的动态 Def 处理把已有的链接搅乱了，就得主动触发一次重链。
    ///
    /// 开销：这两件事在正常情况下各只做一次（自检遇到异常会重试，但最多 MaxIntactAttempts 次），
    ///       之后每 tick 就只剩两次 bool 判断，几乎不花钱。
    ///       一个例外：玩家把「进入地图时自动重新链接」关掉时，重链那件事会一直留着机会
    ///       （他随时可能重新打开），此时每 tick 会多读一两个字段，开销同样可以忽略。
    /// </summary>
    public class CompatGameComponent : GameComponent
    {
        /// <summary>本局游戏是否已经处理过重链。不写进存档，每次载入存档都会重新做一次。</summary>
        [Unsaved]
        private bool relinkHandled;

        /// <summary>本局游戏是否已经跑过完整性自检。同样不写进存档。</summary>
        [Unsaved]
        private bool intactChecked;

        /// <summary>
        /// 完整性自检已经试过几次了。
        ///
        /// 自检失败时故意不把 intactChecked 置位，是为了保住下次重试的机会；
        /// 但这个方法是每 tick 都会被调用的，要是失败原因一直存在，
        /// 就会变成「每 tick 重试一次」。所以必须设一个上限。
        /// </summary>
        [Unsaved]
        private int intactAttempts;

        /// <summary>自检的重试上限。用完之后本局就不再尝试，免得每 tick 白跑一趟。</summary>
        private const int MaxIntactAttempts = 3;

        /// <summary>
        /// 上一次看到的引擎世代号（<see cref="CompatEngine.InitializeGeneration"/>）。
        ///
        /// 玩家在设置界面点「重新扫描」时，引擎会把世代号 +1。这里一旦发现对不上，
        /// 就把「自检做过了没」「自检试过几次」一起清零，让自检重新获得重试机会。
        /// 初始值故意设成 -1：第一局开始时必定与世代号（0）不同，于是自然走一遍初始化。
        /// </summary>
        private int seenInitializeGeneration = -1;

        /// <summary>Log.ErrorOnce 的去重键（取 'UBFC' 的十六进制，只需要在本模组内部唯一即可）。</summary>
        private const int IntactCheckErrorKey = 0x55424643;

        /// <summary>
        /// 重链失败时用的去重键。跟上面那个故意取不同的值，
        /// 这样「自检失败」和「重链失败」各自都能报出第一条，不会互相顶掉。
        /// </summary>
        private const int RelinkErrorKey = 0x55424644;

        /// <summary>
        /// RimWorld 是用 Activator.CreateInstance(type, game) 来创建 GameComponent 的
        /// （见 Verse.Game.FillComponents），所以这个带 Game 参数的构造函数必须留着，
        /// 哪怕它里面一行代码都没有。
        /// 基类 GameComponent 自己就是无参构造，所以这里不用写 base(game)。
        /// 构造函数里什么都不做也是故意的：重活都留到 tick 里去做，
        /// 因为刚创建的那一刻，地图和游戏状态都还没准备好。
        /// </summary>
        public CompatGameComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            // 先看玩家有没有点过「重新扫描」。点过就意味着引擎重跑了一遍初始化，
            // 此时自检的「已检查」「已试几次」都必须清零 —— 否则自检一旦试满上限，
            // 就算数据后来被修好了，它也不会再自动跑。
            if (seenInitializeGeneration != CompatEngine.InitializeGeneration)
            {
                seenInitializeGeneration = CompatEngine.InitializeGeneration;
                intactChecked = false;
                intactAttempts = 0;
            }

            // 两件事都办完了就直接短路返回 —— 这是本组件在稳定状态下每 tick 的全部开销。
            if (intactChecked && relinkHandled)
            {
                return;
            }

            // 注意：下面这个 return 是故意**不**置位任何标志的。
            // 引擎还没就绪的时候，不该白白浪费掉这次机会 ——
            // 留着等条件满足的下一个 tick 再办（反正每 tick 都会再来一次）。
            if (!CompatEngine.Ready)
            {
                return;
            }

            // 完整性自检是故意排在「要不要重链」的判断之前的：
            // 后者管的是「要不要刷新地图上已经建好的那些家具」，
            // 前者修的是「Def 数据本身被别人覆盖了」，两件事根本不是一回事，
            // 所以自检不受「进入地图时自动重新链接」那个开关的影响。
            if (!intactChecked)
            {
                // 下面这三条约束是同时成立才安全的：
                //   1. 只有成功之后才置位 intactChecked —— 失败了要保住重试机会，
                //      同时把异常挡在 tick 循环之外，不让它把游戏循环搅乱；
                //   2. 用 Log.ErrorOnce 而不是 Log.Error —— 本方法每 tick 调用一次，
                //      要是失败原因一直存在，普通的 Error 会变成每秒几十条红字刷屏；
                //   3. 重试次数要封顶 —— 否则一旦失败，每 tick 都要白跑一遍自检。
                //      次数用尽后就置位 intactChecked，本局不再尝试。
                if (intactAttempts >= MaxIntactAttempts)
                {
                    intactChecked = true;
                }
                else
                {
                    try
                    {
                        CompatEngine.EnsureLinkTablesIntact();
                        intactChecked = true;
                    }
                    catch (System.Exception ex)
                    {
                        intactAttempts++;
                        // ErrorOnce 的第二个参数是去重键：同一个键只会报一次，
                        // 后面重复的都会被吞掉，避免刷屏。
                        Log.ErrorOnce(
                            "[UBFC] " + "UBFC_Log_IntactCheckFailed".Translate()
                            + " (attempt " + intactAttempts + "/" + MaxIntactAttempts + ")\n" + ex,
                            IntactCheckErrorKey);
                    }
                }
            }

            if (relinkHandled)
            {
                return;
            }

            // 玩家把「进入地图时自动重新链接」关掉时，同样把这次机会留着 ——
            // 他随时可能重新打开，而到那时候地图多半已经加载好了。
            if (CompatMod.Settings?.relinkOnMapLoad == false)
            {
                return;
            }

            // 链接名单压根没被改动过，说明确实无事可做（比如本局没有任何床用设施），
            // 那就把重链标记成「办完了」，不用每 tick 再来问一遍。
            if (!CompatEngine.LinkTableChanged)
            {
                relinkHandled = true;
                return;
            }

            List<Map> maps = Find.Maps;
            if (maps == null || maps.Count == 0)
            {
                // 地图还没加载完，同样留到下一个 tick 再试。
                return;
            }

            // 逐张地图重链，并且**每张地图各包一层 try/catch**。
            //
            // 为什么这里必须包住：本方法是每 tick 被调用一次的。RelinkMap 会让
            // 地图上每一个设施重新连一次床，过程中会调到别的模组可能覆写过的
            // 虚方法、以及视线判定之类的游戏逻辑；任何一处抛异常，都会顺着
            // tick 循环一路冒出去。那就不是「重链失败」这么简单了 ——
            // 游戏会每 tick 抛一次、红字刷屏到没法玩。
            //
            // 两个细节：
            //   1. 用 Log.ErrorOnce 而不是 Log.Error —— 理由和上面自检那块一样，防刷屏；
            //   2. 不管成功失败，最后都要把 relinkHandled 立起来。
            //      宁可这一局不再自动重链（玩家还能在设置界面点「重新扫描」手动来一次），
            //      也绝不能每 tick 都抛异常。
            for (int i = 0; i < maps.Count; i++)
            {
                try
                {
                    CompatEngine.RelinkMap(maps[i]);
                }
                catch (System.Exception ex)
                {
                    // 一张地图出错不影响其余地图，循环继续往下走。
                    Log.ErrorOnce(
                        "[UBFC] " + "UBFC_Log_RelinkFailed".Translate()
                        + " (map index " + i + ")\n" + ex,
                        RelinkErrorKey);
                }
            }

            relinkHandled = true;
        }
    }
}
