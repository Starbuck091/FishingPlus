using MelonLoader;

[assembly: MelonInfo(typeof(FishingPlus.Core), "FishingPlus", "1.0.0", "Starbuck097", null)]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace FishingPlus
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");
        }
    }
}