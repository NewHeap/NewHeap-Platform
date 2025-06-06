using NewHeap.Platform.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.Mutate
{
    public class AddressMutateModel
    {
        [MaxLength(50)]
        public string AddressCode { get; set; }

        [MaxLength(100)]
        public string Country { get; set; }

        [MaxLength(3)]
        public string CountryCode { get; set; }

        [MaxLength(100)]
        public string Province { get; set; }

        [MaxLength(100)]
        public string Municipality { get; set; }

        [NhRequired]
        [MaxLength(100)]
        public string Place { get; set; }

        [MaxLength(20)]
        public string PostalCode { get; set; }

        [MaxLength(150)]
        public string Street { get; set; }

        [MaxLength(20)]
        public string StreetObjectNumber { get; set; }

        [MaxLength(20)]
        public string StreetObjectNumberSuffix { get; set; }

        [MaxLength(20)]
        public string StreetObjectRoomNumber { get; set; }

        [MaxLength(100)]
        public string LocationDescription { get; set; }
    }
}
