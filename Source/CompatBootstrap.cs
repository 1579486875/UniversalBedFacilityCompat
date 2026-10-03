using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 启动引导：整个模组的「点火钥匙」—— 游戏启动时由它去叫醒 CompatEngine 开始干活。
    ///
    /// 为什么要专门有这么个类？因为本模组要做的事（给每张床补上它能用的家具名单）
    /// 必须等到游戏把 Def 数据全部读完、彼此之间的引用也都连好之后才能做。
    /// RimWorld 恰好提供了这样一个时机：等<b>全部 Def 与跨引用都解析完毕</b>之后，
    /// 游戏会调用 StaticConstructorOnStartupUtility.CallAll()，用反射把每一个
    /// 带 [StaticConstructorOnStartup] 特性的类都跑一遍它的静态构造函数。
    /// 我们就把「开始干活」挂在这个时机上：这时 DefDatabase 已经完整、
    /// 每张床的 linkableFacilities（这张床欢迎哪些家具）已经读进来了、
    /// linkableBuildings（家具能作用于哪些床）也已经被原版推导过一轮了。
    ///
    /// 那为什么不干脆把 [StaticConstructorOnStartup] 直接加在 CompatEngine 上，
    /// 省掉这个类呢？因为 CompatEngine 有可能被 Harmony 补丁在 Def 还在加载的时候提前碰到。
    /// .NET 的规矩是「谁先碰到这个类，就先跑它的静态构造函数」——
    /// 那样就会在 DefDatabase 还没读完的时候提前开扫，扫到一半的数据全是空的。
    /// 拆成两个类之后，「谁先碰到引擎」和「什么时候真正初始化」就成了两件互不相干的事。
    ///
    /// 顺带一提：带 [StaticConstructorOnStartup] 的静态构造函数，由 .NET 运行时保证
    /// 全游戏只跑一次，不需要像 Mod 子类那样自己写防重复的守卫。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CompatBootstrap
    {
        static CompatBootstrap()
        {
            try
            {
                CompatEngine.Initialize();
            }
            catch (System.Exception ex)
            {
                // CompatEngine.Initialize() 内部已经把错误都兜住了，这里再包一层纯粹是双保险，
                // 目的只有一个：万一真出了什么谁都没料到的异常，也只记一条红字，
                // 绝不让它抛出去打断整个游戏的启动流程。
                Log.Error("[UBFC] " + "UBFC_Log_BootstrapFailed".Translate() + "\n" + ex);
            }
        }
    }
}
