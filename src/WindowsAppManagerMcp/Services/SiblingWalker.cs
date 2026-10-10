namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Walks the children of a tree node one sibling at a time, the way a UI Automation TreeWalker
/// does, and keeps going when a child disappears in the middle of the walk.
/// </summary>
/// <remarks>
/// Navigating from an element that has disappeared fails, and retrying from that same element
/// fails again. Instead of giving up on the remaining siblings, the walk resumes from the last
/// sibling it could navigate from, or from the parent's first child. The logic is generic so it
/// can be unit tested without live UI.
/// </remarks>
public static class SiblingWalker
{
    /// <summary>
    /// How many times in a row the walk may resume past a vanished child before it gives up.
    /// Bounds the walk when a provider keeps producing children that vanish immediately.
    /// </summary>
    public const int MaxConsecutiveResumes = 16;

    /// <summary>
    /// Enumerates the children of <paramref name="parent"/> in order.
    /// </summary>
    /// <param name="parent">The node whose children to enumerate.</param>
    /// <param name="getFirstChild">Returns a node's first child, or null. An exception for
    /// <paramref name="parent"/> itself propagates to the caller.</param>
    /// <param name="getNextSibling">Returns a node's next sibling, or null. Throws when the node has disappeared.</param>
    /// <param name="isSameNode">Returns true when two references point to the same node.</param>
    /// <param name="onChildrenLost">Called once if the walk has to stop before reaching the last
    /// child, which means some children may be missing from the enumeration.</param>
    public static IEnumerable<T> EnumerateChildren<T>(
        T parent,
        Func<T, T?> getFirstChild,
        Func<T, T?> getNextSibling,
        Func<T, T, bool> isSameNode,
        Action onChildrenLost)
        where T : class
    {
        // The most recent child that navigation succeeded from, i.e. one still in the tree
        T? lastNavigable = null;
        var consecutiveResumes = 0;

        var child = getFirstChild(parent);
        while (child != null)
        {
            yield return child;

            T? next = null;
            var lost = false;
            try
            {
                next = getNextSibling(child);
                lastNavigable = child;
                consecutiveResumes = 0;
            }
            catch (Exception)
            {
                // The child disappeared after it was visited, so navigation from it fails
                lost = ++consecutiveResumes > MaxConsecutiveResumes ||
                    !TryResume(parent, child, lastNavigable, getFirstChild, getNextSibling, isSameNode, out next);
            }

            if (lost)
            {
                onChildrenLost();
                yield break;
            }

            child = next;
        }
    }

    /// <summary>
    /// Finds the sibling after <paramref name="failedChild"/> by navigating from the last sibling
    /// before it that is still in the tree (or from the parent when there is none).
    /// </summary>
    private static bool TryResume<T>(
        T parent,
        T failedChild,
        T? lastNavigable,
        Func<T, T?> getFirstChild,
        Func<T, T?> getNextSibling,
        Func<T, T, bool> isSameNode,
        out T? next)
        where T : class
    {
        try
        {
            next = lastNavigable != null ? getNextSibling(lastNavigable) : getFirstChild(parent);

            if (next != null && isSameNode(next, failedChild))
            {
                // The child is still in the tree but cannot be navigated from, so there is no way past it
                next = null;
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            next = null;
            return false;
        }
    }
}
