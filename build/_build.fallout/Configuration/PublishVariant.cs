namespace Harbor.Build.Configuration;

public enum PublishVariant
{
    FrameworkDependent,
    SelfContained,
    SingleFile,
    SingleFileSelfContained,
    Trimmed,
    AOT
}
