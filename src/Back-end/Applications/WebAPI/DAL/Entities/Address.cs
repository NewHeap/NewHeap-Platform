using NewHeap.Platform.AspNet.Common.Models;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebAPI.DAL.Entities;

public class Address : IdDbEntity
{
    public Address()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    [StringLength(100)]
    public string Country { get; set; }

    [StringLength(3)]
    public string CountryCode { get; set; }

    [StringLength(100)]
    public string Province { get; set; }

    [StringLength(100)]
    public string Municipality { get; set; }

    [StringLength(100)]
    public string Place { get; set; }

    [StringLength(20)]
    public string PostalCode { get; set; }

    [StringLength(150)]
    public string Street { get; set; }

    [StringLength(20)]
    public string StreetObjectNumber { get; set; }

    [StringLength(20)]
    public string StreetObjectNumberSuffix { get; set; }

    [StringLength(20)]
    public string StreetObjectRoomNumber { get; set; }

    [StringLength(100)]
    public string LocationDescription { get; set; }

    [Column(TypeName = "decimal(11, 6)")]
    public decimal LocationLongitude { get; set; }

    [Column(TypeName = "decimal(11, 6)")]
    public decimal LocationLatitude { get; set; }

    public string ComputedCompleteAddress { get; set; }
}