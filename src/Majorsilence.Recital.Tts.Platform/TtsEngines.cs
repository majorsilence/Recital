namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>The OS voice engine for this platform, if it has one Recital can use.</summary>
    public static class TtsEngines
    {
        /// <param name="cacheDirectory">Where synthesized audio is cached (local app data), or null for no cache.</param>
        public static IReadOnlyList<ITtsEngine> ForCurrentPlatform (string? cacheDirectory)
        {
            var cache = cacheDirectory is null ? null : new AudioCache (cacheDirectory);
            var engines = new List<ITtsEngine> ();
            if (OperatingSystem.IsWindows ())
                engines.Add (Windows (cache));
            else if (OperatingSystem.IsMacOS () && File.Exists ("/usr/bin/say") && AfplayAudioOutput.IsAvailable ())
                engines.Add (Mac (cache));
            else if (SpeechDispatcherEngine.IsAvailable ())
                engines.Add (new SpeechDispatcherEngine ());
            return engines;
        }

        [System.Runtime.Versioning.SupportedOSPlatform ("windows")]
        private static AudioTtsEngine Windows (AudioCache? cache) => new (new SapiSynthesizer (), () => new WaveOutAudioOutput (), cache);

        [System.Runtime.Versioning.SupportedOSPlatform ("macos")]
        private static AudioTtsEngine Mac (AudioCache? cache) => new (new MacSaySynthesizer (), () => new AfplayAudioOutput (), cache);
    }
}
