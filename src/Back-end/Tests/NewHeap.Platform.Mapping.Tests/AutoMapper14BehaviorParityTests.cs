extern alias AutoMapper14;

using System.Globalization;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using NH = NewHeap.Platform.Mapping;
using AM = AutoMapper14::AutoMapper;
using AutoMapper14::AutoMapper.Internal;

using Xunit;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class AutoMapper14BehaviorParityTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void SupportedBehaviorMatchesAutoMapper14(string scenario, string expected, string actual)
    {
        Assert.True(expected == actual, $"{scenario}: AutoMapper 14 = {expected}; NewHeap = {actual}");
    }

    public static IEnumerable<object[]> Cases()
    {
        return RunAudit().Select(result => new object[] { result.Name, result.AutoMapper14, result.NewHeap });
    }

    private static IReadOnlyList<AuditResult> RunAudit()
    {
        var results = new List<AuditResult>();
        var nhNodes = new NH.MapperConfiguration(c => c.CreateMap<NodeSource, NodeView>()).CreateMapper();
        var amNodes = AutoMapperConfiguration(c => c.CreateMap<NodeSource, NodeView>()).CreateMapper();
        // Compare identity within a single operation, not between independent Map calls.
        Check("recursive shared siblings", () => SharedSiblings(amNodes.Map<NodeView>), () => SharedSiblings(nhNodes.Map<NodeView>));
        Check("recursive repeated collection element", () => Repeated(amNodes.Map<NodeView[]>), () => Repeated(nhNodes.Map<NodeView[]>));
        Check("self cycle", () => Cycle(amNodes.Map<NodeView>), () => Cycle(nhNodes.Map<NodeView>));
        Check("shared acyclic graph object count", () => GraphCount(amNodes.Map<NodeView>), () => GraphCount(nhNodes.Map<NodeView>));

        foreach (var depth in new[] { 1, 2 })
        {
            var nhDepth = new NH.MapperConfiguration(c => c.CreateMap<NodeSource, NodeView>().MaxDepth(depth)).CreateMapper();
            var amDepth = AutoMapperConfiguration(c => c.CreateMap<NodeSource, NodeView>().MaxDepth(depth)).CreateMapper();
            Check($"MaxDepth({depth}) self cycle", () => Cycle(amDepth.Map<NodeView>), () => Cycle(nhDepth.Map<NodeView>));
            Check($"MaxDepth({depth}) distinct collection children", () => DepthChildren(amDepth.Map<NodeView>), () => DepthChildren(nhDepth.Map<NodeView>));
        }

        var nhEmpty = new NH.MapperConfiguration(_ => { }).CreateMapper();
        var amEmpty = AutoMapperConfiguration(_ => { }).CreateMapper();
        Check("enum by name with different numeric values", () => (int)amEmpty.Map<NewState>(OldState.Ready), () => (int)nhEmpty.Map<NewState>(OldState.Ready));
        Check("string EnumMember value", () => amEmpty.Map<WireState>("in-progress"), () => nhEmpty.Map<WireState>("in-progress"));
        Check("empty string to enum", () => amEmpty.Map<OldState>(""), () => nhEmpty.Map<OldState>(""));
        Check("string to Guid", () => amEmpty.Map<Guid>("00112233-4455-6677-8899-aabbccddeeff"), () => nhEmpty.Map<Guid>("00112233-4455-6677-8899-aabbccddeeff"));
        Check("string to TimeSpan", () => amEmpty.Map<TimeSpan>("01:02:03"), () => nhEmpty.Map<TimeSpan>("01:02:03"));
        Check("DateTime to DateTimeOffset", () => amEmpty.Map<DateTimeOffset>(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)), () => nhEmpty.Map<DateTimeOffset>(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            Check("decimal string under nl-NL", () => amEmpty.Map<decimal>("1,25"), () => nhEmpty.Map<decimal>("1,25"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        var nhNull = new NH.MapperConfiguration(c => c.CreateMap<string, string>().ConvertUsing(s => s == null ? "fallback" : s)).CreateMapper();
        var amNull = AutoMapperConfiguration(c => c.CreateMap<string, string>().ConvertUsing(s => s == null ? "fallback" : s)).CreateMapper();
        Check("ConvertUsing null root", () => amNull.Map<string, string>(null!), () => nhNull.Map<string, string>(null!));

        var nhConverted = new NH.MapperConfiguration(c =>
        {
            c.CreateMap<TextModel, TextModel>();
            c.CreateMap<string, string>().ConvertUsing(s => s == null ? "fallback" : s);
        }).CreateMapper();
        var amConverted = AutoMapperConfiguration(c =>
        {
            c.CreateMap<TextModel, TextModel>();
            c.CreateMap<string, string>().ConvertUsing(s => s == null ? "fallback" : s);
        }).CreateMapper();
        Check("ConvertUsing null member", () => amConverted.Map<TextModel>(new TextModel()).Value, () => nhConverted.Map<TextModel>(new TextModel()).Value);

        var nhConstruct = new NH.MapperConfiguration(c => c.CreateMap<TextModel, TextModel>().ConstructUsing(_ => new TextModel { Value = "constructed" })).CreateMapper();
        var amConstruct = AutoMapperConfiguration(c => c.CreateMap<TextModel, TextModel>().ConstructUsing(_ => new TextModel { Value = "constructed" })).CreateMapper();
        Check("ConstructUsing null root", () => amConstruct.Map<TextModel, TextModel>(null!), () => nhConstruct.Map<TextModel, TextModel>(null!));

        var nhFlat = new NH.MapperConfiguration(c => c.CreateMap<FlatSource, FlatView>()).CreateMapper();
        var amFlat = AutoMapperConfiguration(c => c.CreateMap<FlatSource, FlatView>()).CreateMapper();
        Check("default convention flattening", () => amFlat.Map<FlatView>(new FlatSource { Owner = new TextModel { Value = "owner" } }), () => nhFlat.Map<FlatView>(new FlatSource { Owner = new TextModel { Value = "owner" } }));

        var nhPrivate = new NH.MapperConfiguration(c => c.CreateMap<TextModel, PrivateView>()).CreateMapper();
        var amPrivate = AutoMapperConfiguration(c => c.CreateMap<TextModel, PrivateView>()).CreateMapper();
        Check("private destination setter", () => amPrivate.Map<PrivateView>(new TextModel { Value = "mapped" }).Value, () => nhPrivate.Map<PrivateView>(new TextModel { Value = "mapped" }).Value);

        var nhAll = new NH.MapperConfiguration(c => c.CreateMap<TextModel, TextModel>().ForAllMembers(o => o.MapFrom(_ => "forced"))).CreateMapper();
        var amAll = AutoMapperConfiguration(c => c.CreateMap<TextModel, TextModel>().ForAllMembers(o => o.MapFrom(_ => "forced"))).CreateMapper();
        Check("ForAllMembers MapFrom", () => amAll.Map<TextModel>(new TextModel { Value = "original" }).Value, () => nhAll.Map<TextModel>(new TextModel { Value = "original" }).Value);

        var nhInclude = new NH.MapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>();
        }).CreateMapper();
        var amInclude = AutoMapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>();
        }).CreateMapper();
        Check("IncludeBase polymorphic base destination", () => amInclude.Map<BaseView>(new DerivedSource()).GetType().Name, () => nhInclude.Map<BaseView>(new DerivedSource()).GetType().Name);
        Check("IncludeBase polymorphic collection", () => amInclude.Map<BaseView[]>(new BaseSource[] { new DerivedSource() })[0].GetType().Name, () => nhInclude.Map<BaseView[]>(new BaseSource[] { new DerivedSource() })[0].GetType().Name);

        var nhCondition = new NH.MapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>().ForAllMembers(o => o.Condition((_, _, _, _) => false));
        }).CreateMapper();
        var amCondition = AutoMapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>().ForAllMembers(o => o.Condition((_, _, _, _) => false));
        }).CreateMapper();
        Check("IncludeBase with derived ForAllMembers condition", () => amCondition.Map<DerivedView>(new DerivedSource { Name = "overwrite" }).Name, () => nhCondition.Map<DerivedView>(new DerivedSource { Name = "overwrite" }).Name);

        Check("ForAllMembers overrides member condition", () => ConditionPriority(true), () => ConditionPriority(false));
        Check("converter skips AfterMap", () => ConverterActions(true), () => ConverterActions(false));

        return results;

        void Check(string name, Func<object?> expected, Func<object?> actual)
        {
            var am = Observe(expected);
            var nh = Observe(actual);
            results.Add(new AuditResult(name, am, nh, am == nh));
        }

    }

    static AM.MapperConfiguration AutoMapperConfiguration(Action<AM.IMapperConfigurationExpression> configure)
    {
        return new AM.MapperConfiguration(configuration =>
        {
            configure(configuration);
            configuration.Internal().ForAllMaps((typeMap, mappingExpression) =>
            {
                if (typeMap.MaxDepth == 0)
                {
                    mappingExpression.MaxDepth(64);
                }
            });
        });
    }

    static string Observe(Func<object?> run)
    {
        try
        {
            return JsonConvert.SerializeObject(run(), new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });
        }
        catch (Exception exception)
        {
            return "ERROR: " + exception.GetType().Name;
        }
    }

    static object SharedSiblings(Func<object, NodeView> map)
    {
        var child = new NodeSource { Name = "shared" };
        child.Child = child;
        var root = new NodeSource { Child = child, Children = [child] };
        var result = map(root);
        return new { Same = ReferenceEquals(result.Child, result.Children[0]), ChildCycle = ReferenceEquals(result.Child, result.Child!.Child) };
    }

    static object Repeated(Func<object, NodeView[]> map)
    {
        var node = new NodeSource();
        node.Child = node;
        var result = map(new[] { node, node });
        return ReferenceEquals(result[0], result[1]);
    }

    static object GraphCount(Func<object, NodeView> map)
    {
        var root = new NodeSource();
        for (var index = 1; index < 12; index++)
        {
            root = new NodeSource { Child = root, Children = [root] };
        }

        var visited = new HashSet<NodeView>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<NodeView>();
        pending.Push(map(root));
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            if (node.Child is not null)
            {
                pending.Push(node.Child);
            }

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        return visited.Count;
    }

    static object Cycle(Func<object, NodeView> map)
    {
        var node = new NodeSource();
        node.Child = node;
        var result = map(node);
        return new { Same = ReferenceEquals(result, result.Child), ChildNull = result.Child == null };
    }

    static object DepthChildren(Func<object, NodeView> map)
    {
        return map(new NodeSource { Name = "1", Children = [new NodeSource { Name = "2", Children = [new NodeSource { Name = "3" }] }] });
    }

    static object? ConditionPriority(bool autoMapper)
    {
        if (autoMapper)
        {
            var mapper = AutoMapperConfiguration(c =>
            {
                var map = c.CreateMap<TextModel, TextModel>();
                map.ForMember(d => d.Value, o => o.Condition((_, _, _, _) => true));
                map.ForAllMembers(o => o.Condition((_, _, _, _) => false));
            }).CreateMapper();
            return mapper.Map(new TextModel { Value = "new" }, new TextModel { Value = "old" }).Value;
        }

        var nh = new NH.MapperConfiguration(c =>
        {
            var map = c.CreateMap<TextModel, TextModel>();
            map.ForMember(d => d.Value, o => o.Condition((_, _, _, _) => true));
            map.ForAllMembers(o => o.Condition((_, _, _, _) => false));
        }).CreateMapper();
        return nh.Map(new TextModel { Value = "new" }, new TextModel { Value = "old" }).Value;
    }

    static object ConverterActions(bool autoMapper)
    {
        var calls = 0;
        if (autoMapper)
        {
            var mapper = AutoMapperConfiguration(c => c.CreateMap<TextModel, TextModel>()
                .AfterMap((_, _) => calls++)
                .ConvertUsing(s => new TextModel { Value = s.Value })).CreateMapper();
            mapper.Map<TextModel>(new TextModel());
        }
        else
        {
            var mapper = new NH.MapperConfiguration(c => c.CreateMap<TextModel, TextModel>()
                .AfterMap((_, _) => calls++)
                .ConvertUsing(s => new TextModel { Value = s.Value })).CreateMapper();
            mapper.Map<TextModel>(new TextModel());
        }

        return calls;
    }

    record AuditResult(string Name, string AutoMapper14, string NewHeap, bool Equal);
    public sealed class NodeSource
    {
        public string? Name { get; set; }
        public NodeSource? Child { get; set; }
        public List<NodeSource> Children { get; set; } = [];
    }
    public sealed class NodeView
    {
        public string? Name { get; set; }
        public NodeView? Child { get; set; }
        public List<NodeView> Children { get; set; } = [];
    }
    public sealed class TextModel
    {
        public string? Value { get; set; }
    }
    public sealed class FlatSource
    {
        public TextModel? Owner { get; set; }
        public string GetLabel() => "label";
    }
    public sealed class FlatView
    {
        public string? OwnerValue { get; set; }
        public string? Label { get; set; }
    }
    public sealed class PrivateView
    {
        public string? Value { get; private set; }
    }
    public class BaseSource
    {
        public string? Name { get; set; }
    }
    public sealed class DerivedSource : BaseSource
    {
        public string? Extra { get; set; }
    }
    public class BaseView
    {
        public string? Name { get; set; }
    }
    public sealed class DerivedView : BaseView
    {
        public string? Extra { get; set; }
    }
    public enum OldState { Unknown = 0, Ready = 1 }
    public enum NewState { Unknown = 0, Ready = 10 }
    public enum WireState
    {
        Unknown = 0,
        [EnumMember(Value = "in-progress")]
        InProgress = 1
    }
}
