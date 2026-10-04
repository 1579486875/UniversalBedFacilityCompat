using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace UniversalBedFacilityCompat
{
    /// <summary>
    /// 模组入口：负责装 Harmony 补丁，以及提供设置界面。
    ///
    /// 时机说明：RimWorld 会在<b>读取 Def 之前</b>就把 Mod 实例创建出来
    /// （LoadedModManager.LoadAllActiveMods → CreateModClasses → LoadModXML → … → ResolveReferences），
    /// 所以在这个阶段装补丁来得及；而真正繁重的扫描工作不在这里做，
    /// 而是放在 <see cref="CompatBootstrap"/> 的 [StaticConstructorOnStartup] 里执行。
    /// </summary>
    public class CompatMod : Mod
    {
        /// <summary>全局设置。CompatEngine 会直接读这里，取值的时候自己做空值兜底。</summary>
        public static CompatSettings Settings;

        private const string HarmonyId = "gnh.cn.cys.universalbedfacilitycompat";

        /// <summary>
        /// 补丁是不是已经成功装过了。
        ///
        /// 这道守卫是必须有的：RimWorld 的 LoadedModManager.CreateModClasses() 每次加载都会被调用
        ///（打开游戏内的「模组」页面也会触发），但它靠 runningModClasses 这个静态字典去重 ——
        /// 该字典从创建起就永不清空（全部读写点只有 CreateModClasses、GetMod 和 LoadedModManager
        /// 的静态构造函数），所以正常路径下同一个 Mod 类只会构造一次。
        /// 真正要防的是「首次构造就抛异常」：runningModClasses[type] = Activator.CreateInstance(...)
        /// 是**先构造、后赋值**，构造抛异常时字典里留不下记录，下次还会再构造一次 ——
        /// 那时补丁叠加才是真风险。
        ///
        /// 而 Harmony 装补丁的方式是「往原方法上再叠一层」，不是替换：
        /// 所以同一处装两次就会有两层，装十次就有十层。
        /// 不加守卫地反复 PatchAll，会让同一个方法上叠满 Postfix，
        /// 表现就是「越操作越卡」，一直卡到死。
        ///
        /// 失败时也照样把它立起来，这是刻意的，理由就是「防补丁叠层」：
        /// PatchAll 是「一个补丁类一个补丁类地装」的，中途抛异常时前面几个很可能已经装上了。
        /// 如果这时留着 false 让下次再 PatchAll 一遍，那几个已装上的补丁就会被叠上第二层 ——
        /// 正是历史上「越操作越卡」的成因。两害相权取其轻：宁可这一局的保险丝没装上
        ///（核心功能完全不依赖 Harmony），也绝不让补丁叠层把游戏拖垮。
        /// </summary>
        private static bool patchesApplied;

        public CompatMod(ModContentPack content) : base(content)
        {
            // GetSettings<T>() 会去磁盘上读玩家的设置文件（没有的话就用默认值），
            // 顺便把它挂到 Mod 基类上，这样设置窗口关闭时基类会自动帮我们存盘。
            Settings = GetSettings<CompatSettings>();
            // 注意：这里只装补丁，不做扫描 —— 扫描必须等 Def 全部加载完，见 CompatBootstrap。
            ApplyHarmonyPatches();
        }

        /// <summary>
        /// 装补丁 —— 整个模组就只装这一个（ResolveReferences 的保险丝）。
        /// 补丁装失败也不影响核心功能，因为核心逻辑完全不依赖 Harmony。
        /// </summary>
        private static void ApplyHarmonyPatches()
        {
            if (patchesApplied)
            {
                // 已经装过了，直接返回 —— 这就是「防重复」的那道守卫。
                return;
            }

            try
            {
                // PatchAll 会扫描这个程序集里所有带 [HarmonyPatch] 的类，逐个装上去。
                new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
                patchesApplied = true;
            }
            catch (System.Exception ex)
            {
                // 失败之后照样把标志立起来，理由：
                // PatchAll 是「一个补丁类一个补丁类地装」的，中途抛异常时，前面几个
                // 很可能已经装上了。如果这里留着 false 让下次再 PatchAll 一次，
                // 那几个已装上的补丁就会被叠上第二层 —— 正是历史上「越操作越卡」的成因。
                // 两害相权取其轻：宁可这一局的保险丝没装上（核心功能完全不依赖 Harmony），
                // 也绝不让补丁叠层把游戏拖垮。
                patchesApplied = true;
                Log.Warning("[UBFC] " + "UBFC_Log_HarmonyFailed".Translate() + "\n" + ex);
            }
        }

        /// <summary>
        /// 设置界面左侧那一栏里显示的模组名，直接复用 About.xml 里的模组名，
        /// 免得在代码里再写死一份文本（将来改名字只用改一个地方）。
        /// </summary>
        public override string SettingsCategory()
        {
            return Content?.Name ?? "Universal Bed Facility Compat";
        }

        /// <summary>
        /// 画设置窗口的内容。只要窗口开着，这个方法就会被逐帧调用，所以里面要尽量省。
        /// 这里刻意只改内存里的设置值，不落盘 —— 落盘由基类 Mod.WriteSettings()
        /// 在窗口关闭时统一做一次，因此本类不需要再去 override 它。
        /// </summary>
        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings == null)
            {
                // 设置对象都没准备好（理论上不该发生），那就不画了，免得报错。
                return;
            }

            var listing = new Listing_Standard();
            listing.Begin(inRect);

            // 下面这两个开关只影响「再扫描一次」的结果，所以运行中随时切换都能立刻生效
            listing.CheckboxLabeled("UBFC_Set_Relink_Label".Translate(),
                ref Settings.relinkOnMapLoad, "UBFC_Set_Relink_Tip".Translate());
            listing.Gap(4f);
            listing.CheckboxLabeled("UBFC_Set_Verbose_Label".Translate(),
                ref Settings.verboseLogging, "UBFC_Set_Verbose_Tip".Translate());

            listing.GapLine(12f);

            // 下面这三个开关会改变 Def 数据本身，所以切换之后必须点一下「重新扫描」才会应用
            listing.CheckboxLabeled("UBFC_Set_Aggressive_Label".Translate(),
                ref Settings.aggressiveMode, "UBFC_Set_Aggressive_Tip".Translate());
            listing.Gap(4f);
            listing.CheckboxLabeled("UBFC_Set_LinkLimit_Label".Translate(),
                ref Settings.ignoreLinkLimit, "UBFC_Set_LinkLimit_Tip".Translate());
            listing.Gap(4f);
            listing.CheckboxLabeled("UBFC_Set_MaxDistance_Label".Translate(),
                ref Settings.fixTooSmallMaxDistance, "UBFC_Set_MaxDistance_Tip".Translate());

            listing.Gap(10f);
            if (listing.ButtonText("UBFC_Button_Rescan".Translate()))
            {
                // 重新扫描可以放心重复点：它会先从基线重建、再按当前设置注入一遍，
                // 所以每次都能精确回到原点，不会越点越乱。
                CompatEngine.Initialize();
            }

            listing.GapLine(12f);
            listing.Label("UBFC_Report_Title".Translate());
            listing.Gap(2f);
            listing.Label(CompatEngine.BuildReport());

            listing.End();
        }
    }
}
