using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CustomModules;
using HarmonyLib;

namespace SaveTranslator
{
    public class SaveTranslatorMod : ModBase {

        public static int LoadOrder = 3;
        public static Type[] LoadAfter() {
            return new Type[] { typeof(NuterraMod) };
        }

        internal static Logger logger;
        public static void ConfigureLogger() {
            Logger.TargetConfig config = new Logger.TargetConfig() {
                layout = "${longdate} | ${level:uppercase=true:padding=-5:alignmentOnTruncation=left} | ${message}  ${exception}",
                keepOldFiles = false
            };
            logger = new Logger("SaveTranslatorMod", config, 4);
        }

        internal static bool inited = false;
        internal const string HarmonyID = "com.floof.terratech.savetranslator";
        private Harmony harmony = new Harmony(HarmonyID);

        public void ManagedEarlyInit() {
            if(!inited) {
                Console.WriteLine("[SaveTranslatorMod] Configuring logger");
                ConfigureLogger();
                inited = true;
            }
        }

        public override void DeInit() {
            harmony.UnpatchAll(HarmonyID);
        }

        public override void Init() {
            harmony.PatchAll();
        }
    }
}
