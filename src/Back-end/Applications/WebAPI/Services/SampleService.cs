using NewHeap.Platform.Common.Models;
using System;

namespace WebAPI.Services
{
    public class SampleModel
    { 
        public Guid Id { get; set; }
    }

    public class SampleModel2
    {
        public Guid Id { get; set; }
    }

    public class SampleModel3
    {
        public Guid Id { get; set; }
    }

    public class SampleService
    {

        public TaskResult<SampleModel> Update(Guid id)
        {
            var result = new TaskResult<SampleModel>();

            var result1 = Check1(id);

            result1.ApplyTo(result);

            if (!result.Success)
            {
                return result;
            }

            var result2 = Check2(id);
            result2.ApplyTo(result);

            if (!result.Success)
            {
                return result;
            }

            var result3 = CheckNoT(id);
            result3.ApplyTo(result);

            if (!result.Success)
            {
                return result;
            }

            return result;
        }

        public TaskResult<SampleModel> UpdateMergedNoCheck(Guid id)
        {
            var result = new TaskResult<SampleModel>();

            var result1 = Check1(id);
            var result2 = Check2(id);
            var result3 = CheckNoT(id);

            result1.ApplyTo(result);
            result2.ApplyTo(result);
            result3.ApplyTo(result);

            return result;
        }

        public TaskResult<SampleModel2> Check1(Guid id)
        {
            var result = new TaskResult<SampleModel2>();

            return result.WithKeylessError("Errortje");
        }

        public TaskResult<SampleModel3> Check2(Guid id)
        {
            var result = new TaskResult<SampleModel3>();

            return result.WithKeylessError("Errortje2");
        }

        public TaskResult CheckNoT(Guid id)
        {
            var result = new TaskResult();

            return result.WithKeylessError("ErrortjeNoT");
        }
    }
}
