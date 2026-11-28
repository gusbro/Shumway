using Shumway.Embedding;

namespace Shumway.Jupyter;

/// <summary>The library collections of the page a notebook session runs in
/// (ADR-070, O3): the ones the page has, which the demo imported as well, and
/// the way <c>jupyter:import_library/1</c> brings in another. Called on the
/// session's engine thread.</summary>
public interface INotebookLibraries
{
    /// <summary>Puts on <paramref name="engine"/>'s search path every
    /// collection the page has. Before each cell, so a collection imported in
    /// another tab, or by another notebook, is there for it.</summary>
    void Refresh(PrologEngine engine);

    /// <summary>Imports a collection and puts it on <paramref name="engine"/>'s
    /// search path; blocks until the page has it. <paramref name="location"/>
    /// is a URL, a file's absolute path, or empty for the collection the page
    /// suggests for <paramref name="dialect"/>. Answers what to tell the
    /// notebook, or throws with why the import failed.</summary>
    string Import(PrologEngine engine, string dialect, string location);
}
