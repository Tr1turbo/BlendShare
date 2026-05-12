namespace Triturbo.BlendShapeShare.Extractor
{
    public enum MeshFeatureExtractionStatus
    {
        Succeeded,
        Skipped,
        Failed
    }

    public readonly struct MeshFeatureExtractionResult
    {
        public MeshFeatureExtractionStatus Status { get; }
        public string Message { get; }
        public bool Succeeded => Status == MeshFeatureExtractionStatus.Succeeded;

        private MeshFeatureExtractionResult(MeshFeatureExtractionStatus status, string message)
        {
            Status = status;
            Message = message;
        }

        public static MeshFeatureExtractionResult Success()
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Succeeded, null);
        }

        public static MeshFeatureExtractionResult Skipped(string message = null)
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Skipped, message);
        }

        public static MeshFeatureExtractionResult Failed(string message)
        {
            return new MeshFeatureExtractionResult(MeshFeatureExtractionStatus.Failed, message);
        }
    }
}
