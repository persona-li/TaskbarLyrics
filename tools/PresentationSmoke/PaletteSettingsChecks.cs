using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;

internal static class PaletteSettingsChecks
{
    public static void Run(Action<bool, string> check, string root)
    {
        var path=System.IO.Path.Combine(root,"oklch-settings");
        using(var config=new ConfigService(_=>{},_=>{},(_,_)=>{},path,watchFile:false,fontExists:_=>true))
        using(var vm=new SettingsViewModel(config,new FakeSettings()))
        {
            config.Update(c=>{
                c.Display.FontSize=37;c.Display.ShadowEnabled=false;c.Display.ShadowColor="#80112233";
                c.General.RecentNormalColors=["#FF102030"];
                c.General.RecentHighlightColors=["#FFABCDEF"];
                c.General.RecentShadowColors=["#80112233"];
            });
            foreach(var preset in PalettePreset.All)
            {
                vm.PresetCommand.Execute(preset);
                var expected=OklchLyricPalette.Generate(preset.Hue);
                check(vm.NormalColor==expected.NormalColor && vm.HighlightColor==expected.HighlightColor && vm.PaletteName==preset.Name,
                    "Preset uses common pair generation: "+preset.Name);
                check(vm.PalettePresets.Count(p=>p.Selected)==1,"Exactly one selected preset");
                check(!vm.ShadowEnabled && vm.ShadowColor=="#80112233" && vm.FontSize==37,
                    "Palette does not alter shadow, font or preview effects");
                check(vm.RecentNormalColors.SequenceEqual(["#FF102030"])
                    && vm.RecentHighlightColors.SequenceEqual(["#FFABCDEF"])
                    && vm.RecentShadowColors.SequenceEqual(["#80112233"]),
                    "Selecting a preset does not write picker-only recent history");
            }
            vm.NormalColor="#FF123456";
            check(vm.PaletteName=="自定义" && vm.PalettePresets.All(p=>!p.Selected),"Independent edit leaves preset mode");
            foreach(var hue in new[]{0d,180d,360d,126.75}) vm.PaletteHue=hue;
            check(vm.PaletteName=="自定义" && vm.NormalColor==OklchLyricPalette.Generate(126.75).NormalColor,
                "Hue drag replaces custom edits with a generated pair immediately");
            check(vm.PreviewConfig.Display.HighlightColor==vm.HighlightColor,"Real preview receives the same generated highlight");
            check(vm.RecentNormalColors.SequenceEqual(["#FF102030"])
                && vm.RecentHighlightColors.SequenceEqual(["#FFABCDEF"])
                && vm.RecentShadowColors.SequenceEqual(["#80112233"]),
                "Hue changes and independent live colors do not write picker-only recent history");
            config.SaveNow();
        }
        using(var config=new ConfigService(_=>{},_=>{},(_,_)=>{},path,watchFile:false,fontExists:_=>true))
        using(var vm=new SettingsViewModel(config,new FakeSettings()))
        {
            check(config.Current.Display.PaletteHue==126.75
                && config.Current.Display.NormalColor==OklchLyricPalette.Generate(126.75).NormalColor
                && config.Current.Display.HighlightColor==OklchLyricPalette.Generate(126.75).HighlightColor,
                "Hue and both generated colors survive restart");
            check(vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Restart reopens generated custom palette without opening manual colors");
            check(config.Current.General.RecentNormalColors.SequenceEqual(["#FF102030"])
                && config.Current.General.RecentHighlightColors.SequenceEqual(["#FFABCDEF"])
                && config.Current.General.RecentShadowColors.SequenceEqual(["#80112233"]),
                "Saving the generated palette leaves all picker histories unchanged on disk");
            config.ResetColorsToDefaults();
            check(config.Current.Display.PaletteHue==OklchLyricPalette.DefaultHue
                && config.Current.Display.NormalColor=="#FF496DBF" && config.Current.Display.HighlightColor=="#FFA0CCEE",
                "Reset restores exact blue pair and original hue");
            check(!vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Reset to a preset removes required customization disclosures");
        }
        CheckDisclosures(check, root);
    }

    private static void CheckDisclosures(Action<bool, string> check, string root)
    {
        var path=System.IO.Path.Combine(root,"palette-disclosures");
        using(var config=new ConfigService(_=>{},_=>{},(_,_)=>{},path,watchFile:false,fontExists:_=>true))
        using(var vm=new SettingsViewModel(config,new FakeSettings()))
        {
            check(!vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Default preset starts with both customization sections closed");
            vm.ManualColorsExpanded=true;
            check(vm.CustomPaletteExpanded && vm.ManualColorsExpanded,
                "Opening manual colors also keeps its parent visible");
            vm.CustomPaletteExpanded=false;
            check(!vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "A preset allows both manually-opened sections to close");
            vm.CustomPaletteExpanded=true;
            vm.ManualColorsExpanded=true;
            vm.PresetCommand.Execute(PalettePreset.All[0]);
            check(vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Clicking the current preset keeps customization expanded");

            vm.PaletteHue=126.75;
            vm.PaletteHue=OklchLyricPalette.DefaultHue;
            check(vm.CustomPaletteExpanded && !vm.ManualColorsExpanded && vm.PaletteName=="原蓝",
                "Dragging across a preset does not hide the hue slider mid-edit");
            vm.CustomPaletteExpanded=false;
            check(!vm.CustomPaletteExpanded,"Preset reached by hue drag can still be collapsed manually");
            vm.NormalColor="#FF112244";
            vm.NormalColor=PalettePreset.All[0].NormalColor;
            check(vm.CustomPaletteExpanded && vm.ManualColorsExpanded && vm.PaletteName=="原蓝",
                "Returning to preset colors from a picker keeps the editing controls visible");
            vm.PresetCommand.Execute(PalettePreset.All[0]);

            vm.PaletteHue=126.75;
            check(vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "A generated non-preset color opens only custom controls");
            var notifications=new List<string?>();
            vm.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
            vm.CustomPaletteExpanded=false;
            check(vm.CustomPaletteExpanded && notifications.Contains(nameof(vm.CustomPaletteExpanded)),
                "Required custom section rejects closing and notifies the binding");
            vm.ManualColorsExpanded=true;
            vm.ManualColorsExpanded=false;
            check(!vm.ManualColorsExpanded,
                "Generated custom colors still allow manual controls to close");
            vm.NormalColor="#FF112244";
            check(vm.CustomPaletteExpanded && vm.ManualColorsExpanded,
                "Independent colors keep both customization sections visible");
            notifications.Clear();
            vm.ManualColorsExpanded=false;
            vm.CustomPaletteExpanded=false;
            check(vm.CustomPaletteExpanded && vm.ManualColorsExpanded
                && notifications.Contains(nameof(vm.ManualColorsExpanded))
                && notifications.Contains(nameof(vm.CustomPaletteExpanded)),
                "Independent colors reject closing either required section and notify bindings");
            check(vm.RecentNormalColors.Count==0 && vm.RecentHighlightColors.Count==0 && vm.RecentShadowColors.Count==0,
                "Disclosure changes and live colors do not write recent color history");
            config.SaveNow();
            var saved=System.IO.File.ReadAllText(config.ConfigPath);
            check(!saved.Contains("customPaletteExpanded",StringComparison.OrdinalIgnoreCase)
                && !saved.Contains("manualColorsExpanded",StringComparison.OrdinalIgnoreCase),
                "Disclosure state is derived rather than added to persisted configuration");
        }
        using(var config=new ConfigService(_=>{},_=>{},(_,_)=>{},path,watchFile:false,fontExists:_=>true))
        using(var vm=new SettingsViewModel(config,new FakeSettings()))
        {
            check(vm.CustomPaletteExpanded && vm.ManualColorsExpanded,
                "Restart derives both required disclosures from persisted independent colors");
            var preset=PalettePreset.All[3];
            config.Update(c=>{
                c.Display.NormalColor="#"+preset.NormalColor[3..].ToLowerInvariant();
                c.Display.HighlightColor=preset.HighlightColor.ToLowerInvariant();
            });
            check(vm.PaletteHue==126.75 && vm.PaletteName==preset.Name
                && vm.PalettePresets.Single(p=>p.Selected).Name==preset.Name,
                "Actual preset colors are recognized independently of stored hue and HEX spelling");
            check(!vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Restoring actual preset colors permits both sections to close despite stale hue");
            vm.CustomPaletteExpanded=true;
            vm.ManualColorsExpanded=true;
            vm.CustomPaletteExpanded=false;
            check(!vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Recognized preset with stale hue accepts user collapse");
            config.SaveNow();
        }
        using(var config=new ConfigService(_=>{},_=>{},(_,_)=>{},path,watchFile:false,fontExists:_=>true))
        using(var vm=new SettingsViewModel(config,new FakeSettings()))
        {
            check(vm.PaletteHue==126.75 && vm.PaletteName==PalettePreset.All[3].Name
                && !vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Restart recognizes preset colors without rewriting hue or opening manual controls");
            vm.CustomPaletteExpanded=true;
            vm.ManualColorsExpanded=true;
            vm.PresetCommand.Execute(PalettePreset.All[2]);
            check(vm.PaletteName==PalettePreset.All[2].Name
                && vm.CustomPaletteExpanded && !vm.ManualColorsExpanded,
                "Selecting another preset keeps customization expanded");
        }
    }
}
