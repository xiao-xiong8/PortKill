using PortKiller.UpdateManagement.ServerUpdates;

if (!OperatingSystem.IsLinux()) return 2;
try
{
    if (args is ["bootstrap", var deployment, var project, var expectedSha, var socketGroup])
        return await ServerUpdateBootstrap.RunAsync(deployment, project, expectedSha, socketGroup);
    if (args is ["recover", var requestPath])
        return await new ServerUpdateRunner(new DockerCommand()).RecoverAsync(ServerUpdateFiles.Read<RunnerRequest>(requestPath));
    if (args.Length != 1) return 2;
    var request = ServerUpdateFiles.Read<RunnerRequest>(args[0]);
    return await new ServerUpdateRunner(new DockerCommand()).RunAsync(request);
}
catch (Exception error)
{
    Console.Error.WriteLine("Server update runner failed: " + error.GetType().Name);
    return 1;
}
