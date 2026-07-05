using Microsoft.AspNetCore.Components.Web;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// The to-do surface: a topbar button + drawer over the SAME todos the analyst already
// maintains via its add_todo / complete_todo tools (Db.ListTodos). The user can see them,
// mark them done, or send a question/update straight into the chat where the analyst answers.
public partial class Home
{
    private bool _showTodos;
    private List<TodoItem> _todos = new();
    private int _openTodoCount;               // drives the topbar badge
    private long? _todoAsking;                // id of the todo whose inline ask box is open
    private string _todoAskDraft = "";

    /// <summary>Reload the list + open-count. Called on thread load, drawer open, and after a
    /// reply (the analyst may have added/closed todos mid-turn).</summary>
    private void RefreshTodos()
    {
        if (!Database.IsOpen) { _todos = new(); _openTodoCount = 0; return; }
        _todos = Database.ListTodos();
        _openTodoCount = _todos.Count(t => !t.Done);
    }

    private void ToggleTodos()
    {
        _showTodos = !_showTodos;
        _todoAsking = null; _todoAskDraft = "";
        if (_showTodos) { _showNotifs = false; _showMenu = false; RefreshTodos(); }
    }

    private void CloseTodos() { _showTodos = false; _todoAsking = null; _todoAskDraft = ""; }

    /// <summary>Flip a todo done/open (durable SQLite write) and refresh the badge.</summary>
    private void ToggleTodoDone(TodoItem t)
    {
        if (!Database.IsOpen) return;
        Database.SetTodoDone(t.Id, !t.Done);
        RefreshTodos();
        StateHasChanged();
    }

    private void StartAskTodo(TodoItem t) { _todoAsking = t.Id; _todoAskDraft = ""; }
    private void CancelAskTodo() { _todoAsking = null; _todoAskDraft = ""; }

    private async Task TodoAskKey(KeyboardEventArgs e, TodoItem t)
    {
        if (e.Key == "Enter" && !e.ShiftKey) await SendAskTodo(t);
    }

    /// <summary>Send a question/update about a specific todo into the main chat. It rides the normal
    /// Send() pipeline (persist + analyst reply), with the todo quoted so the analyst knows the
    /// context — and can complete_todo / adjust it in the same turn.</summary>
    private async Task SendAskTodo(TodoItem t)
    {
        var msg = _todoAskDraft.Trim();
        if (msg.Length == 0 || _busy) return;
        _draft = $"Re: my to-do “{t.Text}” — {msg}";
        CloseTodos();
        if (_showConstellation) await ToggleConstellation();   // surface the reply in the chat view
        await Send();
    }

    /// <summary>Local short due-date label ("Aug 3"), or null when the todo has no due date.</summary>
    private static string? TodoDue(TodoItem t) =>
        t.DueUtc is { } d && DateTime.TryParse(d, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToLocalTime().ToString("MMM d") : null;
}
