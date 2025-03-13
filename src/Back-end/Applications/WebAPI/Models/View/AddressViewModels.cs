using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;

namespace WebAPI.Models.View
{
    public class AddressCollectionRequestModel : CollectionRequestModel
    {
        public List<string> CountryCodes { get; set; } = new();
    }

    public class PublicAddressRequestModel : SearchableBaseCollectionRequestModel
    {
        public List<string> CountryCodes { get; set; } = new();
    }

    public class AddressViewModel
    {
        [Searchable]
        public Guid Id { get; set; }

        [Searchable, Orderable, Filterable]
        public string AddressCode { get; set; }

        [Searchable, Orderable, Filterable]
        public DateTimeOffset CreationDateTime { get; set; }

        [Searchable, Orderable, Filterable]
        public DateTimeOffset LastModifiedDateTime { get; set; }

        [Searchable, Orderable, Filterable]
        public string Country { get; set; }

        [Searchable, Orderable, Filterable]
        public string CountryCode { get; set; }

        [Searchable, Orderable, Filterable]
        public string Province { get; set; }

        [Searchable, Orderable, Filterable]
        public string Municipality { get; set; }

        [Searchable, Orderable, Filterable]
        public string Place { get; set; }

        [Searchable, Orderable, Filterable]
        public string PostalCode { get; set; }

        [Searchable, Orderable, Filterable]
        public string Street { get; set; }

        [Searchable, Orderable, Filterable]
        public string StreetObjectNumber { get; set; }

        [Searchable, Orderable, Filterable]
        public string StreetObjectNumberSuffix { get; set; }

        [Searchable, Orderable, Filterable]
        public string StreetObjectRoomNumber { get; set; }

        [Searchable, Orderable, Filterable]
        public string LocationDescription { get; set; }

        [Searchable, Orderable, Filterable]
        public decimal LocationLongitude { get; set; }

        [Searchable, Orderable, Filterable]
        public decimal LocationLatitude { get; set; }

        public string IdentifiableKey { get; set; }

        public string ComputedCompleteAddress { get; set; }
    }
}
