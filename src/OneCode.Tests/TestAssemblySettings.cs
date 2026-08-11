// The application under test has process-global state (current directory,
// environment variables and embedded configuration watchers). Running those
// tests in parallel makes unrelated tests mutate each other's process state.
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
