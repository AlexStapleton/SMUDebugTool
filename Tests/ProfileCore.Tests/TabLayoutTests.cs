using System.Collections.Generic;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class TabLayoutTests
    {
        private static readonly string[] Canonical = { "a", "b", "c", "d" };

        [Fact]
        public void OrderedVisibleKeys_preserves_canonical_order_and_excludes_hidden()
        {
            var hidden = new HashSet<string> { "b" };

            var visible = TabLayout.OrderedVisibleKeys(Canonical, hidden);

            Assert.Equal(new[] { "a", "c", "d" }, visible.ToArray());
        }

        [Fact]
        public void OrderedVisibleKeys_with_no_hidden_returns_all_in_order()
        {
            var visible = TabLayout.OrderedVisibleKeys(Canonical, new HashSet<string>());

            Assert.Equal(Canonical, visible.ToArray());
        }

        [Fact]
        public void OrderedVisibleKeys_tolerates_null_hidden()
        {
            var visible = TabLayout.OrderedVisibleKeys(Canonical, null);

            Assert.Equal(Canonical, visible.ToArray());
        }

        [Fact]
        public void ResolveStartupTabKey_returns_default_when_visible()
        {
            var key = TabLayout.ResolveStartupTabKey(Canonical, new HashSet<string>(), "c");

            Assert.Equal("c", key);
        }

        [Fact]
        public void ResolveStartupTabKey_falls_back_to_first_visible_when_default_hidden()
        {
            var hidden = new HashSet<string> { "a", "c" };

            var key = TabLayout.ResolveStartupTabKey(Canonical, hidden, "c");

            Assert.Equal("b", key);
        }

        [Fact]
        public void ResolveStartupTabKey_falls_back_to_first_visible_when_default_empty_or_unknown()
        {
            Assert.Equal("a", TabLayout.ResolveStartupTabKey(Canonical, new HashSet<string>(), null));
            Assert.Equal("a", TabLayout.ResolveStartupTabKey(Canonical, new HashSet<string>(), ""));
            Assert.Equal("a", TabLayout.ResolveStartupTabKey(Canonical, new HashSet<string>(), "nonexistent"));
        }

        [Fact]
        public void ResolveStartupTabKey_returns_null_when_all_hidden()
        {
            var hidden = new HashSet<string>(Canonical);

            var key = TabLayout.ResolveStartupTabKey(Canonical, hidden, "a");

            Assert.Null(key);
        }
    }
}
