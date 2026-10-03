using System.Collections.Generic;
using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 兼容策略 Def：留给玩家和模组作者的一个「逃生舱」。
    ///
    /// 本模组的自动分类已经能正确处理绝大多数情况，但有两种场合需要人工干预：
    /// 1. 某件家具明明不是给床用的，却被判成了床用设施（开着激进模式时更容易发生）。
    /// 2. 某张床刻意不希望被设施影响（比如特殊的观赏床）。
    ///
    /// 用法：在自己的模组里写一个 PatchOperation，把 defName 追加到对应名单里就行：
    ///
    /// <code>
    /// &lt;Operation Class="PatchOperationAdd"&gt;
    ///   &lt;xpath&gt;Defs/UniversalBedFacilityCompat.CompatPolicyDef[defName="UBFC_DefaultPolicy"]/excludedFacilities&lt;/xpath&gt;
    ///   &lt;value&gt;&lt;li&gt;SomeMod_DecorativeDresser&lt;/li&gt;&lt;/value&gt;
    /// &lt;/Operation&gt;
    /// </code>
    ///
    /// 也允许另外新增策略 Def：引擎会把**所有**策略 Def 的名单合并起来一起用，
    /// 所以谁先加载、谁后加载都不影响结果（不用担心自己的补丁被别人覆盖掉）。
    ///
    /// 这是一个普通 Def，所以 label / description 走的是 DefInjected 翻译机制，
    /// 不会出现写死在代码里的文本。
    /// </summary>
    public class CompatPolicyDef : Def
    {
        /// <summary>排除名单：名单上的这些设施不参与「通用床铺链接」，也就是不再对模组床生效。</summary>
        public List<ThingDef> excludedFacilities = new List<ThingDef>();

        /// <summary>排除名单：名单上的这些床不接收通用设施链接，保持原版行为（该怎么着还怎么着）。</summary>
        public List<ThingDef> excludedBeds = new List<ThingDef>();
    }
}
