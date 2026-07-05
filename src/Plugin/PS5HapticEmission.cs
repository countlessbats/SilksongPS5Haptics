namespace SilksongPS5Haptics
{
    /// <summary>
    /// A playing PS5 haptic clip. Position is advanced by the HapticEngine mix
    /// thread; the game controls it through the VibrationEmission surface
    /// (Stop, SetStrength, SetSpeed, SetPlaybackTime, looping).
    /// </summary>
    public sealed class PS5HapticEmission : VibrationEmission
    {
        internal readonly HapticPcm Pcm;
        internal double PositionFrames;   // guarded by HapticEngine mix lock
        private volatile bool isPlaying;
        private bool isLooping;
        private string tag;
        private VibrationTarget target;
        private readonly bool isRealtime;

        public PS5HapticEmission(HapticPcm pcm, float baseStrength, bool isLooping, string tag, VibrationTarget target, bool isRealtime)
        {
            Pcm = pcm;
            this.isLooping = isLooping;
            this.tag = tag;
            this.target = target;
            this.isRealtime = isRealtime;
            BaseStrength = baseStrength;
            isPlaying = true;
        }

        public override VibrationTarget Target { get => target; set => target = value; }
        public override bool IsLooping { get => isLooping; set => isLooping = value; }
        public override string Tag { get => tag; set => tag = value; }
        public override bool IsRealtime => isRealtime;
        public override bool IsPlaying => isPlaying;

        public override float Time
        {
            get => (float)(PositionFrames / HapticEngine.SampleRate);
            set => PositionFrames = value * HapticEngine.SampleRate;
        }

        public override void Play() => isPlaying = true;
        public override void Stop() => isPlaying = false;

        /// Called from the mix thread after accumulating this emission's block.
        internal void Advance(double frames)
        {
            PositionFrames += frames;
            if (PositionFrames >= Pcm.FrameCount)
            {
                if (isLooping)
                    PositionFrames %= Pcm.FrameCount;
                else
                    isPlaying = false;
            }
        }
    }
}
