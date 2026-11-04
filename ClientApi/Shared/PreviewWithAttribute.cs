using System;

namespace Adk.Mvvm
{
    /// <summary>
    /// The value a view model's property shows in the IDE preview. From it the UI generator generates the view model's
    /// preview clone (<c>FooViewModelPreview.Default</c>, in a design source MDK does not pack), whose single values
    /// are design properties the Rider plugin edits. Values: constants; a Color as "#RRGGBB" or "#AARRGGBB"; a vector
    /// as "x y z w" (one value fills all, two fill as a thickness); an item count on a collection of view models.
    /// Several values cycle over the items of a list (item i takes value i).
    /// On a view model class, the first value is a member path and the rest its values: <c>"Title"</c> sets an
    /// inherited member for this view model only, <c>"Rows.Name"</c> a member of the items (or nested view model) of
    /// its member Rows. The most derived class wins over its bases and over the member's own [PreviewWith].
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true, Inherited = true)]
    public sealed class PreviewWithAttribute : Attribute
    {
        public PreviewWithAttribute(params object[] values)
        {
        }
    }
}
