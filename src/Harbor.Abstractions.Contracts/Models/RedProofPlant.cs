// RED-INTERMEDIATE FIXTURE — DELETE ME.
//
// This file is planted by exactly one commit on this branch, whose only purpose
// is to make `ContractsArtifactCapabilityRule.The_Allowed_Probe_Is_Exactly_One_Site`
// go RED in CI, so the guard is proved able to fail rather than merely asserted to
// be. It is the second direction that matters: the first is that the rule reports
// the permitted site, and this one is that the permission is scoped to ONE site and
// not to "a disk call exists somewhere".
//
// The next commit on the branch deletes it. Nothing else reads it, nothing
// references it, and it is not a permission row — it is a violation, planted so
// that a violation is observable.

namespace Harbor.Abstractions.Models;

internal static class RedProofPlant
{
    internal static bool Also(string path) => File.Exists(path);
}
