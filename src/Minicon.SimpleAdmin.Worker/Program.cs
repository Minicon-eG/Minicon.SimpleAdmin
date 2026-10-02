using Minicon.SimpleAdmin.Worker;

// Entry point for running the worker package directly (e.g. as a dotnet tool).
// All bootstrapping lives in SimpleAdminWorkerHost so host applications can
// reuse it with a one-line Main of their own.
return await SimpleAdminWorkerHost.RunAsync(args);
