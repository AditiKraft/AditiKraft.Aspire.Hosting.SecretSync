using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Push);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath AspireDirectory => RootDirectory / "aspire";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PackageOutputDirectory => ArtifactsDirectory / "Packages";

    AbsolutePath PackageProjectPath =>
        SourceDirectory / "AditiKraft.Aspire.Hosting.SecretSync.csproj";

    [Parameter("NuGet API Key for publishing templates")] private readonly string NuGetPAT;
    [Parameter("Package version (default: 0.0.10)")] private readonly string PackageVersion = "0.0.17";

    private AbsolutePath SampleProjectPath =>
        AspireDirectory / "AditiKraft.Aspire.Hosting.SecretSync.AppHost" / "AditiKraft.Aspire.Hosting.SecretSync.AppHost.csproj";

    #region NuGet

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(absolutePath => absolutePath.DeleteDirectory());
            PackageOutputDirectory.GlobFiles().ForEach(absolutePath => absolutePath.DeleteDirectory());
            ArtifactsDirectory.CreateOrCleanDirectory();
            PackageOutputDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(s => s
                .SetProjectFile(SampleProjectPath)
                .SetConfiguration(Configuration)
                .SetProperty("Version", PackageVersion)
                .SetProperty("AssemblyVersion", PackageVersion)
                .EnableNoRestore());
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTasks.DotNetPack(s => s
                .SetConfiguration(Configuration.Release.ToString())
                .SetProject(PackageProjectPath)
                .SetVersion(PackageVersion)
                .SetOutputDirectory(PackageOutputDirectory)
                .EnableIncludeSymbols()
                .SetSymbolPackageFormat("snupkg")
            );
        });

    Target Push => _ => _
        .DependsOn(Pack)
        .Executes(() => PackageOutputDirectory.GlobFiles("*.nupkg")
                .Where(x => !x.Name.EndsWith("symbols.nupkg"))
                .ForEach(x => DotNetTasks.DotNetNuGetPush(s => s
                        .SetTargetPath(x)
                        .SetSource("https://api.nuget.org/v3/index.json")
                        .SetApiKey(NuGetPAT)
                        .EnableSkipDuplicate())));

    #endregion
}
