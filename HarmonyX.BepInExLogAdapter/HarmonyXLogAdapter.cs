using System.Collections.Generic;
using HarmonyLib.Tools;

namespace HarmonyX
{
    public static class HarmonyXLogAdapter
    {
        // List of assemblies to patch
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Initialize()
        {
            var config = (BepInEx.Configuration.ConfigFile)HarmonyLib.AccessTools.Property(typeof(BepInEx.Configuration.ConfigFile), "CoreConfig").GetValue(null, null);
            var il = config.Bind("HarmonyX", "Log IL messages", false, "Write messages from IL channel to BepInEx log (Full IL dumps of the generated dynamic methods)").Value;
            var info = config.Bind("HarmonyX", "Log Info messages", false, "Write messages from informational channel to BepInEx log").Value;
            var warn = config.Bind("HarmonyX", "Log Warn messages", true, "Write messages from warning channel to BepInEx log").Value;
            var error = config.Bind("HarmonyX", "Log Error messages", true, "Write messages from error channel to BepInEx log").Value;

            Logger.ChannelFilter = Logger.LogChannel.None;
            if (il) Logger.ChannelFilter |= Logger.LogChannel.IL;
            if (info) Logger.ChannelFilter |= Logger.LogChannel.Info;
            if (warn) Logger.ChannelFilter |= Logger.LogChannel.Warn;
            if (error) Logger.ChannelFilter |= Logger.LogChannel.Error;

            var logSource = new BepInEx.Logging.ManualLogSource("HarmonyX");
            BepInEx.Logging.Logger.Sources.Add(logSource);

            Logger.MessageReceived += (sender, args) =>
            {
                switch (args.LogChannel)
                {
                    case Logger.LogChannel.Info:
                        logSource.LogInfo(args.Message);
                        break;
                    case Logger.LogChannel.IL:
                        logSource.LogDebug(args.Message);
                        break;
                    case Logger.LogChannel.Warn:
                        logSource.LogWarning(args.Message);
                        break;
                    case Logger.LogChannel.Error:
                        logSource.LogError(args.Message);
                        break;
                }
            };
        }

        public static void Patch(Mono.Cecil.AssemblyDefinition assembly) { }
    }
}