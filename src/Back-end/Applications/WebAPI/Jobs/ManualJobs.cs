using AutoMapper;
using System;

namespace WebAPI.Jobs;

public class ManualJobs
{
    private readonly IMapper _mapper;

    public ManualJobs(
        IMapper mapper
    )
    {
        _mapper = mapper ?? throw new NullReferenceException();
    }
}