using System.Collections.Generic;
using System.Linq;

namespace Stashie
{
    public class BaseFilter : IIFilter
    {
        public List<IIFilter> Filters { get; } = new List<IIFilter>();
        public bool BAny { get; set; }

        public bool CompareItem(ItemData itemData)
        {
            return BAny ? Filters.Any(x => x.CompareItem(itemData)) : Filters.All(x => x.CompareItem(itemData));
        }
    }
}