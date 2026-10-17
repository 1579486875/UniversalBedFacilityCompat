// ---------------------------------------------------------------------------
// 本文件由 AI 助手「DeepSeek娘·汐蓝」辅助编写。
// 设计、需求、审阅与实机验证由人类作者（大赢经直插白皮赢道）完成。
// AI-assisted: written with DeepSeek Niang · Xi Lan (AI);
// designed, reviewed and validated by the human author.
// ---------------------------------------------------------------------------

using HarmonyLib;
using RimWorld;
using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 本模组<b>唯一</b>的一处 Harmony 补丁，而且只挂在 Def 加载这条冷路径上，
    /// 和 Tick / Update / 画面渲染完全没关系，所以对运行时性能没有影响。
    ///
    /// 它是干什么的（一根保险丝）：
    ///   CompProperties_Facility.linkableBuildings（这件家具能作用于哪些建筑）是按
    ///   「床那边的名单」推导出来的。如果在游戏运行期间有人又调了一次 ResolveReferences
    ///   （比如别的模组做动态 Def 处理、开发者工具、热重载等等），推导结果就会退回成
    ///   「只覆盖原版床」，本模组辛苦做出的效果会被悄悄抹掉。
    ///   有了这个 Postfix（后缀方法，也就是「等原方法跑完之后接着跑」的那一段），
    ///   无论名单什么时候被重建，只要这件设施被判定为「床用设施」，
    ///   它的链接名单都会被立刻补齐到全部床。
    ///
    /// 重要：就算补丁装载失败（比如玩家压根没启用 Harmony），核心功能依然是完整的，
    ///       只是少了这一层保险 —— CompatMod 会捕获异常并给出警告。
    ///
    /// 已知的覆盖边界：ResolveReferences 在 1.6/1.7 里都是 CompProperties_Facility 上的
    /// `public override void ResolveReferences(ThingDef parentDef)`（覆写自
    /// Verse.CompProperties.ResolveReferences，两版方法签名一致，已反编译核对）。
    /// 本补丁打的就是 CompProperties_Facility **自己这个方法体**，
    /// 所以要是某个模组派生了 CompProperties_Facility 的子类、又 override 了这个方法
    /// 却不调用 base，那条路径就落在保险丝的保护范围之外了
    /// （这是 Harmony 对覆写方法的常规限制，不是本模组特有的毛病）。
    /// 那种情况下，该子类自己的逻辑本来就是最后说了算的那一个，所以这个边界可以接受。
    /// </summary>
    [HarmonyPatch(typeof(CompProperties_Facility), nameof(CompProperties_Facility.ResolveReferences))]
    internal static class Patch_CompProperties_Facility_ResolveReferences
    {
        [HarmonyPostfix]
        internal static void Postfix(CompProperties_Facility __instance, ThingDef parentDef)
        {
            // __instance 是 Harmony 的一个约定名，指「被补丁的那个对象本身」，
            // 这里就是这件设施的组件配置；parentDef 则是原方法本来就有的参数
            // （这件设施所在的 ThingDef），按名字原样接下来即可。
            CompatEngine.OnFacilityResolveReferences(__instance, parentDef);
        }
    }
}
