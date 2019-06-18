//https://cakebuild.net/dsl/
#tool nuget:?package=GitVersion.CommandLine&version=4.0.0-beta0012
#tool nuget:?package=OctopusTools&version=6.7.0

#addin nuget:?package=Cake.Npm&version=0.17.0
#addin "nuget:?package=Cake.Curl&version=4.1.0"


#load build/paths.cake
#load build/version.cake
#load build/package.cake
#load build/urls.cake

var target = Argument("Target", "Build");
//You can use multiple types:
//var target = Argument<string>("Target", "Build");

Setup<PackageMetadata>(context => {
    return new PackageMetadata(
        outputDirectory: Argument("packageOutputDirectory", "packages"),
        name: "Linker-9");
});

Teardown(context => {
    if (DirectoryExists(Paths.PublishDirectory))
        DeleteDirectory(
            Paths.PublishDirectory,
            new DeleteDirectorySettings {
                Recursive = true
            });
});

//Taskname is not case sensitive
Task("Compile")
    .Does(() => {
        DotNetCoreBuild(Paths.SolutionFile.FullPath);
    });

Task("Test")
    .IsDependentOn("Compile")
    .Does(() => {
        // BOTHS WORKS
        //DotNetCoreTest(Paths.TestProjectFile.FullPath);
        DotNetCoreTest(Paths.SolutionFile.FullPath);
    });

Task("Version")
    .Does<PackageMetadata>(package => {
        package.Version =  ReadVersionFromProjectFile(Context);

        if (package.Version == null || package.Version == string.Empty)
        {
            package.Version = GitVersion().FullSemVer;
        }
        Information($"Determined version number: {package.Version}");
    });

Task("Build-Frontend")
    .Does(() => {
        Information("Build Frontend");
        NpmInstall(settings => settings.FromPath(Paths.FrontendDirectory));
        NpmRunScript("build", settings => settings.FromPath(Paths.FrontendDirectory));
    });

Task("Package-Zip")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Test")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package => {
        CleanDirectory(package.OutputDirectory);

        package.Extension = "zip";

        DotNetCorePublish(
            project: Paths.WebProjectFile.GetDirectory().FullPath,
            new DotNetCorePublishSettings {
                OutputDirectory = Paths.PublishDirectory,
                NoBuild = true /* Already done by compile task */,
                NoRestore = true /* Already done by compile task */,
                MSBuildSettings = new DotNetCoreMSBuildSettings {
                    NoLogo = true
                }
            }
        );

        Zip(Paths.PublishDirectory, package.FullPath);
    });

Task("Package-Octopus")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Test")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package => {
        CleanDirectory(package.OutputDirectory);

        package.Extension = "nupkg";

        DotNetCorePublish(
            project: Paths.WebProjectFile.GetDirectory().FullPath,
            new DotNetCorePublishSettings {
                OutputDirectory = Paths.PublishDirectory,
                NoBuild = true /* Already done by compile task */,
                NoRestore = true /* Already done by compile task */,
                MSBuildSettings = new DotNetCoreMSBuildSettings {
                    NoLogo = true
                }
            });

        OctoPack(
            package.Name,
            new OctopusPackSettings {
                Format = OctopusPackFormat.NuPkg,
                Version = package.Version,
                BasePath = Paths.PublishDirectory,
                OutFolder = package.OutputDirectory
            });

    });

Task("Deploy-Kudu")
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package => {
        CurlUploadFile(
            package.FullPath,
            Urls.KuduDeployUrl,
            new CurlSettings {
                Username = EnvironmentVariable("DeploymentUser"),
                Password = EnvironmentVariable("DeploymentPassword"),
                RequestCommand = "POST",
                ProgressBar = true,
                ArgumentCustomization = args => args.Append("--fail")
            });
    });

Task("Deploy-Octopus")
    .IsDependentOn("Package-Octopus")
    .Does<PackageMetadata>(package => {
        OctoPush(
            Urls.OctopusServerUrl.AbsoluteUri,
            EnvironmentVariable("OctopusApiKey"),
            package.FullPath,
            new OctopusPushSettings {
                EnableServiceMessages = true,
                /*ReplaceExisting = true  ONLY FOR DEVELOPMENT OF CAKE SCRIPT */
            });

        OctoCreateRelease(
            "Linker-9",
            new CreateReleaseSettings {
                Server = Urls.OctopusServerUrl.AbsoluteUri,
                ApiKey = EnvironmentVariable("OctopusApiKey"),
                ReleaseNumber = package.Version,
                DefaultPackageVersion = package.Version,
                DeployTo = "Test" /* Candidate for Script Argument */,
                IgnoreExisting = true,
                EnableServiceMessages = true,
                WaitForDeployment = true
            });
    });

RunTarget(target);
