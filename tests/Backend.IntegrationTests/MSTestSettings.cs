// Each test boots its own host against its own temporary database, so tests can run in parallel.
[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
