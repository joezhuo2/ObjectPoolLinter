using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    // OPL006: a field of managed type in a Unity job struct. Scheduling copies the struct into native
    // memory, which the garbage collector cannot see, so the job system refuses any struct holding a
    // reference and Schedule() throws InvalidOperationException. The compiler accepts it; this rule
    // moves the failure from play mode to the build.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class JobStructAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL006";

        private const string Category = "Usage";
        private static readonly LocalizableString Title = "Managed field in job struct";
        private static readonly LocalizableString MessageFormat = "Field '{0}' of job struct '{1}' {2}. Unity throws InvalidOperationException when the job is scheduled.";
        private static readonly LocalizableString Description = "A struct implementing a Unity job interface (IJob, IJobFor, IJobParallelFor, IJobParallelForTransform, ...) is copied to native memory when it is scheduled, so it may hold only value types and native containers. A field of a class, interface, delegate, array or string type, or of a struct that holds one, makes Schedule() throw InvalidOperationException.";

        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL006.md";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri
        );

        // Job interfaces from Unity, the Collections package and Entities. Any other interface marked
        // [JobProducerType], which every job interface Unity schedules carries, counts too.
        private static readonly string[] JobInterfaceMetadataNames =
        {
            "Unity.Jobs.IJob",
            "Unity.Jobs.IJobFor",
            "Unity.Jobs.IJobParallelFor",
            "Unity.Jobs.IJobParallelForBatch",
            "Unity.Jobs.IJobParallelForDefer",
            "Unity.Jobs.IJobFilter",
            "Unity.Jobs.IJobParallelForFilter",
            "UnityEngine.Jobs.IJobParallelForTransform",
            "Unity.Entities.IJobChunk",
            "Unity.Entities.IJobEntity",
        };

        private const string JobProducerTypeMetadataName = "Unity.Jobs.LowLevel.Unsafe.JobProducerTypeAttribute";

        // NativeArray<T> and the other native containers hold a managed DisposeSentinel when safety
        // checks are on; the job system allows it, and the field marked
        // [NativeSetClassTypeToNullOnSchedule] that holds it.
        private const string NativeContainerMetadataName = "Unity.Collections.LowLevel.Unsafe.NativeContainerAttribute";
        private const string NativeSetClassTypeToNullOnScheduleMetadataName = "Unity.Collections.LowLevel.Unsafe.NativeSetClassTypeToNullOnScheduleAttribute";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var compilation = context.Compilation;

            var jobInterfaces = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var name in JobInterfaceMetadataNames)
            {
                var type = compilation.GetTypeByMetadataName(name);
                if (type != null) jobInterfaces.Add(type);
            }

            var jobProducerType = compilation.GetTypeByMetadataName(JobProducerTypeMetadataName);
            if (jobInterfaces.Count == 0 && jobProducerType == null) return;

            var analyzer = new CompilationAnalyzer(
                jobInterfaces.ToImmutable(),
                jobProducerType,
                compilation.GetTypeByMetadataName(NativeContainerMetadataName),
                compilation.GetTypeByMetadataName(NativeSetClassTypeToNullOnScheduleMetadataName));

            context.RegisterSymbolAction(analyzer.AnalyzeType, SymbolKind.NamedType);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly ImmutableHashSet<INamedTypeSymbol> _jobInterfaces;
            private readonly INamedTypeSymbol? _jobProducerType;
            private readonly INamedTypeSymbol? _nativeContainer;
            private readonly INamedTypeSymbol? _nativeSetClassTypeToNullOnSchedule;

            internal CompilationAnalyzer(
                ImmutableHashSet<INamedTypeSymbol> jobInterfaces,
                INamedTypeSymbol? jobProducerType,
                INamedTypeSymbol? nativeContainer,
                INamedTypeSymbol? nativeSetClassTypeToNullOnSchedule)
            {
                _jobInterfaces = jobInterfaces;
                _jobProducerType = jobProducerType;
                _nativeContainer = nativeContainer;
                _nativeSetClassTypeToNullOnSchedule = nativeSetClassTypeToNullOnSchedule;
            }

            internal void AnalyzeType(SymbolAnalysisContext context)
            {
                var type = (INamedTypeSymbol)context.Symbol;
                if (type.TypeKind != TypeKind.Struct || !type.AllInterfaces.Any(IsJobInterface)) return;

                foreach (var (member, memberType) in GetInstanceStorage(type))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    var problem = DescribeManaged(memberType);
                    if (problem == null) continue;

                    var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location == null) continue;

                    context.ReportDiagnostic(Diagnostic.Create(Rule, location, member.Name, type.Name, problem));
                }
            }

            private bool IsJobInterface(INamedTypeSymbol type)
            {
                if (_jobInterfaces.Contains(type.OriginalDefinition)) return true;

                return _jobProducerType != null && type.OriginalDefinition.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _jobProducerType));
            }

            // Null when the type is safe in a job: a value type holding no reference, a native
            // container, or an unconstrained type parameter, whose type argument Unity checks itself.
            private string? DescribeManaged(ITypeSymbol type)
            {
                if (type.IsReferenceType) return "has the reference type '" + Display(type) + "'";

                var path = FindReference(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), out var reference);
                if (path == null) return null;

                return "has the type '" + Display(type) + "', which holds '" + path + "' of reference type '" + Display(reference!) + "'";
            }

            // The dotted path to the first reference-typed field inside the struct `type`, or null.
            private string? FindReference(ITypeSymbol type, HashSet<ITypeSymbol> visited, out ITypeSymbol? reference)
            {
                reference = null;

                if (type.TypeKind != TypeKind.Struct || type.SpecialType != SpecialType.None) return null;
                if (HasAttribute(type.OriginalDefinition, _nativeContainer)) return null;
                if (!visited.Add(type)) return null;

                foreach (var (member, memberType) in GetInstanceStorage((INamedTypeSymbol)type))
                {
                    if (memberType.IsReferenceType)
                    {
                        reference = memberType;
                        return member.Name;
                    }

                    var inner = FindReference(memberType, visited, out reference);
                    if (inner != null) return member.Name + "." + inner;
                }

                return null;
            }

            // Instance fields and auto-properties, each with the type it stores. A field marked
            // [NativeSetClassTypeToNullOnSchedule] is cleared by the job system and left out.
            private IEnumerable<(ISymbol Member, ITypeSymbol Type)> GetInstanceStorage(INamedTypeSymbol type)
            {
                foreach (var member in type.GetMembers())
                {
                    if (member.IsStatic) continue;

                    switch (member)
                    {
                        case IFieldSymbol { IsConst: false } field:
                            if (HasAttribute(field, _nativeSetClassTypeToNullOnSchedule)) continue;

                            // A type from metadata lists an auto-property's backing field; name it after
                            // the property. A type in source lists the property, handled below.
                            if (field.AssociatedSymbol is IPropertySymbol backed)
                            {
                                if (backed.DeclaringSyntaxReferences.IsEmpty) yield return (backed, field.Type);
                                continue;
                            }

                            if (field.IsImplicitlyDeclared) continue;
                            yield return (field, field.Type);
                            break;

                        case IPropertySymbol property when IsAutoProperty(property):
                            yield return (property, property.Type);
                            break;
                    }
                }
            }

            // `int Count { get; set; }`: every accessor without a body, and the property not abstract or
            // extern. Only a property in source can be told apart this way.
            private static bool IsAutoProperty(IPropertySymbol property)
            {
                if (property.IsAbstract || property.IsExtern || property.IsIndexer) return false;

                var references = property.DeclaringSyntaxReferences;
                if (references.IsEmpty) return false;

                return references.All(reference =>
                    reference.GetSyntax() is PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } accessors } &&
                    accessors.Accessors.All(accessor => accessor.Body == null && accessor.ExpressionBody == null));
            }

            private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
            {
                return attributeType != null && symbol.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));
            }

            private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }
}
