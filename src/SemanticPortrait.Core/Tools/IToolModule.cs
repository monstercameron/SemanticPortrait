namespace SemanticPortrait.Core;

/// <summary>Common shape shared by the tool modules that participate in the Handles-chain
/// dispatch (main-agent Exec, AnalystSubagent ReflectAsync/ImportAsync). The fallback module
/// in each chain (ProfileTools) is NOT part of this interface — it's invoked directly.</summary>
public interface IToolModule
{
    bool Handles(string name);
    Task<string> ExecuteAsync(string name, string args);
}

/// <summary>Walks tool modules in order (first Handles wins), then the fallback — preserving
/// the exact semantics of the hand-written if/else-if chains it replaces. Order is load-bearing:
/// callers must pass modules in the same order as the chain they're replacing.</summary>
public static class ToolDispatch
{
    public static async Task<string> RouteAsync(
        IEnumerable<IToolModule> modules, Func<string, string, Task<string>> fallback, string name, string args)
    {
        foreach (var m in modules)
            if (m.Handles(name)) return await m.ExecuteAsync(name, args);
        return await fallback(name, args);
    }
}
