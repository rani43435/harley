/// Copyright (c)  2024.5 by 东风破

using System.Runtime.InteropServices;

namespace SherpaOnnx
{
    [StructLayout(LayoutKind.Sequential)]
    public struct OfflineTtsModelConfig
    {
        public void Init()
        {
            Vits = new OfflineTtsVitsModelConfig(); Vits.Init();
            Matcha = new OfflineTtsMatchaModelConfig(); Matcha.Init();
            Kokoro = new OfflineTtsKokoroModelConfig(); Kokoro.Init();
            Kitten = new OfflineTtsKittenModelConfig(); Kitten.Init();
            ZipVoice = new OfflineTtsZipVoiceModelConfig(); ZipVoice.Init();
            Pocket = new OfflineTtsPocketModelConfig(); Pocket.Init();
            Supertonic = new OfflineTtsSupertonicModelConfig(); Supertonic.Init();
            NumThreads = 1;
            Debug = 0;
            Provider = "cpu";
        }

        public OfflineTtsVitsModelConfig Vits;
        public int NumThreads;
        public int Debug;

        [MarshalAs(UnmanagedType.LPStr)]
        public string Provider;

        public OfflineTtsMatchaModelConfig Matcha;
        public OfflineTtsKokoroModelConfig Kokoro;
        public OfflineTtsKittenModelConfig Kitten;
        public OfflineTtsZipVoiceModelConfig ZipVoice;
        public OfflineTtsPocketModelConfig Pocket;
        public OfflineTtsSupertonicModelConfig Supertonic;
    }
}
