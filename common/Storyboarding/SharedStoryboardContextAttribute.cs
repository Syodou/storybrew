using System;

namespace StorybrewCommon.Storyboarding
{
    /// <summary>
    /// Marks a storyboard generator as participating in a shared storyboard context.
    /// Generators sharing the same key operate on the same runtime layers and objects.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class SharedStoryboardContextAttribute : Attribute
    {
        /// <summary>
        /// Initializes the attribute with an optional key.
        /// When omitted, the generator's full type name is used as the context key.
        /// </summary>
        public SharedStoryboardContextAttribute(string key = null)
        {
            Key = string.IsNullOrWhiteSpace(key) ? null : key;
        }

        /// <summary>
        /// Optional grouping key that determines which scripts share the same context.
        /// </summary>
        public string Key { get; }
    }
}
