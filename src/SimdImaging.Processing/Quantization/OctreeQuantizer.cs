using SimdImaging.Abstractions;
using SimdImaging.Processing.Internal;

namespace SimdImaging.Processing;

/// <summary>
/// Octree quantizer: every colour descends eight levels, one bit of R, G and B per level, so colours that
/// share a prefix share a subtree. When the leaf count exceeds the budget, the least populated node at the
/// deepest occupied level is collapsed into its parent — the colours it held become one average.
/// <para>
/// The tree is pruned during insertion, not after: a full-resolution photograph has more distinct colours
/// than it has pixels worth of memory, and an unpruned eight-level tree would allocate a node per distinct
/// colour. Pruning keeps the live tree at <c>16 × maxColors</c> leaves, so the cost is bounded by the pixel
/// count rather than by the colour diversity. Insertion order is the scan order, so the result is
/// deterministic (<c>D2</c>) with no random seeding.
/// </para>
/// </summary>
public static class OctreeQuantizer
{
    private const int Depth = 8;                 // one level per bit of a channel
    private const int ChildrenPerNode = 8;
    private const int MinimumLeafBudget = 256;

    /// <summary>Builds a palette of at most <paramref name="maxColors"/> colours.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxColors"/> is outside 2..256.</exception>
    public static ColorPalette Build(PixelView source, int maxColors)
    {
        if (maxColors is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maxColors), maxColors, "A palette holds 2..256 colours.");

        // Prune at 16× the budget: pruning is the expensive step, so it pays to let the tree grow a little
        // past the target and merge in one sweep instead of once per pixel.
        var tree = new Tree(Math.Max(MinimumLeafBudget, maxColors * 16), maxColors);
        tree.Insert(source);
        tree.ReduceTo(maxColors);
        return tree.ToPalette();
    }

    private sealed class Node
    {
        /// <summary>Creates a branch node, or a leaf when <paramref name="leaf"/> is set.</summary>
        public Node(bool leaf = false)
        {
            if (!leaf)
                Children = new Node?[ChildrenPerNode];
        }

        /// <summary>Eight children, or null when the node is a leaf (depth 8, or collapsed).</summary>
        public Node?[]? Children;

        /// <summary>Pixels that ended up on this node. Only meaningful on a leaf.</summary>
        public long Count;

        public long SumR;
        public long SumG;
        public long SumB;

        /// <summary>Creation order, the tie-break that keeps pruning independent of sort stability.</summary>
        public int Order;

        public bool IsLeaf => Children is null;
    }

    private sealed class Tree
    {
        private readonly Node _root = new();
        private readonly List<Node>?[] _reducible = new List<Node>?[Depth];   // nodes at depth 1..7
        private readonly int _leafBudget;
        private readonly int _maxColors;
        private int _leafCount;
        private int _nextOrder;

        public Tree(int leafBudget, int maxColors)
        {
            _leafBudget = leafBudget;
            _maxColors = maxColors;
            for (var level = 1; level < Depth; level++)
                _reducible[level] = new List<Node>();
        }

        public void Insert(PixelView source)
        {
            if (source.Format == WorkingFormat.Rgba8)
            {
                // Byte path: the tree consumes 0..255 codes directly, so a float round trip per row would be
                // pure overhead (and it was measurable in the histogram quantizers).
                for (var y = 0; y < source.Height; y++)
                {
                    var row = source.Row(y);
                    for (var x = 0; x < source.Width; x++)
                    {
                        var offset = x * 4;
                        Add(row[offset], row[offset + 1], row[offset + 2]);
                    }
                }

                return;
            }

            var floatRow = new float[source.Width * 4];
            for (var y = 0; y < source.Height; y++)
            {
                Pixels.ReadRowRaw(source.Row(y), source.Width, source.Format, floatRow);
                for (var x = 0; x < source.Width; x++)
                {
                    var offset = x * 4;
                    Add(ToByte(floatRow[offset]), ToByte(floatRow[offset + 1]), ToByte(floatRow[offset + 2]));
                }
            }
        }

        private void Add(byte r, byte g, byte b)
        {
            var node = _root;
            for (var depth = 0; depth < Depth; depth++)
            {
                if (node.Children is null)
                {
                    // The node was collapsed while this colour was in flight; it now represents the pixel.
                    node.Count++;
                    node.SumR += r;
                    node.SumG += g;
                    node.SumB += b;
                    return;
                }

                var shift = 7 - depth;
                var index = (((r >> shift) & 1) << 2) | (((g >> shift) & 1) << 1) | ((b >> shift) & 1);
                var child = node.Children[index];
                if (child is null)
                {
                    child = new Node(leaf: depth + 1 == Depth);
                    node.Children[index] = child;
                    child.Order = _nextOrder++;
                    if (depth + 1 < Depth)
                        _reducible[depth + 1]!.Add(child);
                    else
                        _leafCount++;
                }

                node = child;
            }

            node.Count++;
            node.SumR += r;
            node.SumG += g;
            node.SumB += b;

            if (_leafCount > _leafBudget)
                ReduceTo(_maxColors);
        }

        /// <summary>
        /// Collapses the least populated nodes at the deepest occupied level until the leaf count fits. Only
        /// the deepest level is touched, so by the time a shallower node is taken every child it has is
        /// already a leaf and the merge is a plain sum.
        /// </summary>
        public void ReduceTo(int target)
        {
            while (_leafCount > target)
            {
                var level = DeepestOccupiedLevel();
                if (level < 0)
                    return;

                var list = _reducible[level]!;
                var candidates = new List<Node>(list.Count);
                foreach (var node in list)
                {
                    if (!node.IsLeaf)
                        candidates.Add(node);
                }

                list.Clear();
                if (candidates.Count == 0)
                    continue;

                // Least populated first; ties fall back to creation order so the result does not depend on
                // the sort implementation or on how equal keys happened to be arranged.
                candidates.Sort(static (a, b) =>
                {
                    var delta = SubtreeCount(a).CompareTo(SubtreeCount(b));
                    return delta != 0 ? delta : a.Order.CompareTo(b.Order);
                });

                var collapsedAny = false;
                var index = 0;
                for (; index < candidates.Count && _leafCount > target; index++)
                {
                    Collapse(candidates[index]);
                    collapsedAny = true;
                }

                for (; index < candidates.Count; index++)
                    list.Add(candidates[index]);

                if (!collapsedAny)
                    return;
            }
        }

        private int DeepestOccupiedLevel()
        {
            for (var level = Depth - 1; level >= 1; level--)
            {
                foreach (var node in _reducible[level]!)
                {
                    if (!node.IsLeaf)
                        return level;
                }
            }

            return -1;
        }

        private void Collapse(Node node)
        {
            long count = 0, r = 0, g = 0, b = 0;
            var children = 0;

            foreach (var child in node.Children!)
            {
                if (child is null)
                    continue;

                count += child.Count;
                r += child.SumR;
                g += child.SumG;
                b += child.SumB;
                children++;
            }

            node.Children = null;
            node.Count += count;
            node.SumR += r;
            node.SumG += g;
            node.SumB += b;
            _leafCount -= Math.Max(0, children - 1);
        }

        private static long SubtreeCount(Node node)
        {
            if (node.Children is null)
                return node.Count;

            long total = 0;
            foreach (var child in node.Children)
            {
                if (child is not null)
                    total += SubtreeCount(child);
            }

            return total;
        }

        public ColorPalette ToPalette()
        {
            var colors = new List<RgbaColor>(_maxColors);
            Collect(_root, colors);
            return new ColorPalette(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(colors));
        }

        private static void Collect(Node node, List<RgbaColor> colors)
        {
            if (node.Children is null)
            {
                if (node.Count > 0)
                {
                    colors.Add(new RgbaColor(
                        (byte)((node.SumR + node.Count / 2) / node.Count),
                        (byte)((node.SumG + node.Count / 2) / node.Count),
                        (byte)((node.SumB + node.Count / 2) / node.Count),
                        255));
                }

                return;
            }

            foreach (var child in node.Children)
            {
                if (child is not null)
                    Collect(child, colors);
            }
        }

        private static byte ToByte(float value)
        {
            var scaled = value * 255f + 0.5f;
            if (scaled <= 0f) return 0;
            if (scaled >= 255f) return 255;
            return (byte)scaled;
        }
    }
}
