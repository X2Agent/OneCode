using OneCode.Tests.TestSupport;

if (args.Length > 0 && args[0] == "--probe")
    return await WorkflowRecoveryProbe.RunAsync(args[1..]).ConfigureAwait(false);

return 0;
