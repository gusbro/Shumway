// Per-stack-walk state for the Shumway call stack filter (ADR-035 spike).
// DkmDataItem pattern from the MIT ConcordExtensibilitySamples HelloWorld sample.

using Microsoft.VisualStudio.Debugger;
using Microsoft.VisualStudio.Debugger.CallStack;

namespace Shumway.Debugger.Concord
{
    internal sealed class ShumwayStackDataItem : DkmDataItem
    {
        private ShumwayStackDataItem()
        {
        }

        /// <summary>How many interpreter frames this walk has replaced so far.</summary>
        public int ReplacedFrames { get; set; }

        public static ShumwayStackDataItem GetInstance(DkmStackContext context)
        {
            ShumwayStackDataItem? item = context.GetDataItem<ShumwayStackDataItem>();
            if (item != null)
                return item;

            item = new ShumwayStackDataItem();
            context.SetDataItem(DkmDataCreationDisposition.CreateNew, item);
            return item;
        }
    }
}
