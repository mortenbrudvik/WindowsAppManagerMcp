namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for SiblingWalker, which walks a UI Automation element's children and keeps
/// going when a child disappears mid-walk. Uses a fake tree, so no real UI is read.
/// </summary>
public class SiblingWalkerTests
{
    /// <summary>
    /// Walks the children of <paramref name="parent"/> and lets the test act on each child as it is
    /// visited, the way the UI element traversal reads properties and recurses before moving on.
    /// </summary>
    private static (List<string> Visited, int LostCount) Walk(FakeNode parent, Action<FakeNode>? onVisit = null)
    {
        var visited = new List<string>();
        var lostCount = 0;

        foreach (var child in SiblingWalker.EnumerateChildren(
            parent,
            FakeNode.GetFirstChild,
            FakeNode.GetNextSibling,
            ReferenceEquals,
            () => lostCount++))
        {
            visited.Add(child.Name);
            onVisit?.Invoke(child);
        }

        return (visited, lostCount);
    }

    [Fact]
    public void EnumerateChildren_WithStableTree_VisitsEveryChildInOrder()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A", "B", "C");

        // Act
        var (visited, lostCount) = Walk(parent);

        // Assert
        visited.Should().Equal("A", "B", "C");
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WithNoChildren_VisitsNothing()
    {
        // Act
        var (visited, lostCount) = Walk(FakeNode.WithChildren());

        // Assert
        visited.Should().BeEmpty();
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WhenChildDisappearsAfterVisit_ContinuesWithLaterSiblings()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A", "B", "C", "D");

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name == "B") parent.Remove(child);
        });

        // Assert
        visited.Should().Equal("A", "B", "C", "D");
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WhenFirstChildDisappears_ResumesFromParent()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A", "B", "C");

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name == "A") parent.Remove(child);
        });

        // Assert
        visited.Should().Equal("A", "B", "C");
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WhenLastChildDisappears_EndsNormally()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A", "B", "C");

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name == "C") parent.Remove(child);
        });

        // Assert
        visited.Should().Equal("A", "B", "C");
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WhenConsecutiveChildrenDisappear_ContinuesWithLaterSiblings()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A", "B", "C", "D", "E");

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name is "B" or "C" or "D") parent.Remove(child);
        });

        // Assert
        visited.Should().Equal("A", "B", "C", "D", "E");
        lostCount.Should().Be(0);
    }

    [Fact]
    public void EnumerateChildren_WhenChildIsPresentButCannotBeNavigatedFrom_StopsWithoutRevisitingAndReportsLoss()
    {
        // Arrange - navigation from B fails although B is still in the tree
        var parent = FakeNode.WithChildren("A", "B", "C");
        parent.Child("B").NavigationFails = true;

        // Act
        var (visited, lostCount) = Walk(parent);

        // Assert
        visited.Should().Equal("A", "B");
        lostCount.Should().Be(1);
    }

    [Fact]
    public void EnumerateChildren_WhenResumeNavigationAlsoFails_StopsAndReportsLoss()
    {
        // Arrange - A and B both disappear while B is being visited
        var parent = FakeNode.WithChildren("A", "B", "C");

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name == "B")
            {
                parent.Remove(parent.Child("A"));
                parent.Remove(child);
            }
        });

        // Assert
        visited.Should().Equal("A", "B");
        lostCount.Should().Be(1);
    }

    [Fact]
    public void EnumerateChildren_WhenEveryResumedChildDisappears_StopsAfterBoundedResumes()
    {
        // Arrange - a provider that keeps producing children that vanish as soon as they are visited
        var parent = FakeNode.WithChildren(Enumerable.Range(0, 1000).Select(i => $"N{i}").ToArray());

        // Act
        var (visited, lostCount) = Walk(parent, child =>
        {
            if (child.Name != "N0") parent.Remove(child);
        });

        // Assert - walking stops instead of chasing vanishing children forever
        visited.Count.Should().BeLessThan(1000);
        visited.Should().StartWith(new[] { "N0", "N1" });
        lostCount.Should().Be(1);
    }

    [Fact]
    public void EnumerateChildren_WhenFirstChildOfParentCannotBeRead_Throws()
    {
        // Arrange
        var parent = FakeNode.WithChildren("A");
        parent.FirstChildFails = true;

        // Act
        var act = () => Walk(parent);

        // Assert - the caller decides what a failed parent means
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// A node in a fake UI tree. Like a UI Automation element, a node that has been removed from
    /// its parent can no longer be navigated from.
    /// </summary>
    private sealed class FakeNode
    {
        private readonly List<FakeNode> _children = new();
        private FakeNode? _parent;

        private FakeNode(string name) => Name = name;

        public string Name { get; }

        public bool NavigationFails { get; set; }

        public bool FirstChildFails { get; set; }

        public static FakeNode WithChildren(params string[] names)
        {
            var parent = new FakeNode("parent");
            foreach (var name in names)
            {
                parent._children.Add(new FakeNode(name) { _parent = parent });
            }

            return parent;
        }

        public FakeNode Child(string name) => _children.Single(c => c.Name == name);

        public void Remove(FakeNode child)
        {
            _children.Remove(child);
            child._parent = null;
        }

        public static FakeNode? GetFirstChild(FakeNode node)
        {
            if (node.FirstChildFails)
            {
                throw new InvalidOperationException("Provider failed");
            }

            return node._children.FirstOrDefault();
        }

        public static FakeNode? GetNextSibling(FakeNode node)
        {
            if (node._parent == null || node.NavigationFails)
            {
                throw new InvalidOperationException($"Element {node.Name} is not available");
            }

            var siblings = node._parent._children;
            var index = siblings.IndexOf(node);
            return index + 1 < siblings.Count ? siblings[index + 1] : null;
        }
    }
}
