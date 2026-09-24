using Xunit;

// This assembly's tests all share one process-wide Postgres + IntegreSQL pair (see
// IntegreSqlPool) and, since Task 5, several of them also spin up a full ASP.NET Core host with
// its own pooled DB connections and a polling BackgroundService. Running test classes in parallel
// piled up enough concurrent load against that single shared Postgres container to make IntegreSQL
// intermittently answer template/test-checkout requests with 423/503/500 in CI (a resource-limited
// 2-core runner), even with the per-hash serialization in IntegreSqlPool. These are integration
// tests hitting real containers, not unit tests — sequential execution trades a slower total run
// for a deterministic one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
