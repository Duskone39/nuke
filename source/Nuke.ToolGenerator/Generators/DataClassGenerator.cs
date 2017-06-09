// Copyright Matthias Koch 2017.
// Distributed under the MIT License.
// https://github.com/matkoch/Nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Nuke.ToolGenerator.Model;
using Nuke.ToolGenerator.Writers;

// ReSharper disable UnusedMethodReturnValue.Local

namespace Nuke.ToolGenerator.Generators
{
    public static class DataClassGenerator
    {
        public static void Run (DataClass dataClass, ToolWriter toolWriter)
        {
            var writer = new DataClassWriter(dataClass, toolWriter);
            var baseType = dataClass.BaseClass ?? (dataClass.Name.EndsWith("Settings") ? "ToolSettings" : "ISettingsEntity");

            writer
                    .WriteSummary(writer.Tool)
                    .WriteLine("[PublicAPI]")
                    .WriteLine("[ExcludeFromCodeCoverage]")
                    .WriteLine("[Serializable]")
                    .WriteLine($"public partial class {dataClass.Name} : {baseType}")
                    .WriteBlock(w => w
                            .WriteToolPath()
                            .ForEach(dataClass.Properties, WritePropertyDeclaration)
                            .WriteAssertValid()
                            .WriteGetArgumentsInternal());
        }

        private static DataClassWriter WriteToolPath (this DataClassWriter writer)
        {
            var settingsClass = writer.DataClass as SettingsClass;
            if (settingsClass == null)
                return writer;

            string expression;
            if (settingsClass.PackageId != null)
                expression =
                        $"NuGetPackageResolver.GetToolPath({settingsClass.PackageId.Quote()}, {(settingsClass.PackageExecutable ?? "{GetExecutable()}").Quote()})";
            else if (settingsClass.EnvironmentExecutable != null)
                expression = $"EnvironmentInfo.EnsureVariable({settingsClass.EnvironmentExecutable.Quote()})";
            else
                expression = settingsClass.CustomExecutable;

            if (expression == null)
                return writer;

            return writer
                .WriteSummaryInherit()
                .WriteLine ($"public override string ToolPath => base.ToolPath ?? {expression};");
        }

        private static void WritePropertyDeclaration (DataClassWriter writer, Property property)
        {
            writer
                    .WriteSummary(property)
                    .WriteLine(GetPublicPropertyDeclaration(property))
                    .WriteLine(GetInternalPropertyDeclarationOrNull(property));
        }

        [CanBeNull]
        private static string GetInternalPropertyDeclarationOrNull (Property property)
        {
            if (!property.IsList() && !property.IsDictionary() && !property.IsLookupTable())
                return null;

            return $"internal {property.Type} {property.Name}Internal {{ get; set; }}{GetPropertyInitialization(property)}";
        }

        private static string GetPublicPropertyDeclaration (Property property)
        {
            var type = GetPublicPropertyType(property);
            var implementation = GetPublicPropertyImplementation(property);
            return $"public virtual {type} {property.Name} {implementation}";
        }

        private static string GetPropertyInitialization (Property property)
        {
            string initializationExpression;
            if (property.IsList())
                initializationExpression = $"new {property.Type}()";
            else if (property.IsDictionary() ||property.IsLookupTable())
                initializationExpression = $"new {property.Type}(StringComparer.OrdinalIgnoreCase)";
            else if (property.IsBoolean() && property.Negate)
                initializationExpression = "true";
            else
                initializationExpression = property.Default;

            return initializationExpression != null
                ? $" = {initializationExpression};"
                : string.Empty;
        }

        private static string GetPublicPropertyImplementation (Property property)
        {
            return property.IsList() || property.IsDictionary() || property.IsLookupTable()
                ? $"=> {property.Name}Internal.AsReadOnly();"
                : $"{{ get; internal set; }}{GetPropertyInitialization(property)}";
        }

        private static string GetPublicPropertyType (Property property)
        {
            if (property.IsList())
            {
                return $"IReadOnlyList<{property.GetListValueType()}>";
            }

            if (property.IsDictionary())
            {
                var (keyType, valueType) = property.GetDictionaryKeyValueTypes();
                return $"IReadOnlyDictionary<{keyType}, {valueType}>";
            }

            if (property.IsLookupTable())
            {
                var (keyType, valueType) = property.GetDictionaryKeyValueTypes();
                return $"ILookup<{keyType}, {valueType}>";
            }

            return property.Type;
        }


        private static DataClassWriter WriteAssertValid (this DataClassWriter writer)
        {
            var validatedProperties = writer.DataClass.Properties.Where(x => x.Assertion != null).ToList();
            if (validatedProperties.Count == 0)
                return writer;

            return writer
                    .WriteSummaryInherit()
                    .WriteLine("protected override void AssertValid()")
                    .WriteBlock(w => w
                            .WriteLine("base.AssertValid();")
                            .ForEach(
                                validatedProperties.Select(GetAssertion),
                                assertion => w.WriteLine($"ControlFlow.Assert({assertion}, {assertion.Quote()});"))
                    );
        }

        private static string GetAssertion (Property property)
        {
            switch (property.Assertion)
            {
                case AssertionType.NotNull:
                    return $"{property.Name} != null";
                case AssertionType.File:
                    return $"File.Exists({property.Name})";
                case AssertionType.Directory:
                    return $"Directory.Exists({property.Name})";
                case AssertionType.FileOrNull:
                    return $"File.Exists({property.Name}) || {property.Name} == null";
                case AssertionType.DirectoryOrNull:
                    return $"Directory.Exists({property.Name}) || {property.Name} == null";
                default:
                    throw new NotSupportedException(property.Assertion.ToString());
            }
        }

        private static DataClassWriter WriteGetArgumentsInternal (this DataClassWriter writer)
        {
            var formatProperties = writer.DataClass.Properties.Where(x => x.Format != null).ToList();
            if (writer.DataClass.DefiniteArgument == null && formatProperties.Count == 0)
                return writer;

            var argumentAdditions = formatProperties.Select(GetArgumentAddition).ToArray();
            ref var last = ref argumentAdditions[argumentAdditions.Length - 1];
            last += ";";

            return writer
                    .WriteLine("protected override Arguments GetArgumentsInternal()")
                    .WriteBlock(w => w
                            .WriteLine("return base.GetArgumentsInternal()")
                            .WriteLine(GetCommandAdditionOrNull(writer.DataClass))
                            .ForEachWriteLine(argumentAdditions));
        }

        [CanBeNull]
        private static string GetCommandAdditionOrNull (DataClass dataClass)
        {
            return dataClass.DefiniteArgument != null
                ? $"  .Add({dataClass.DefiniteArgument.Quote()})"
                : null;
        }

        private static string GetArgumentAddition (Property property)
        {
            var arguments = new List<string>
                            {
                                property.Format.Quote(interpolation: false),
                                GetPropertyFormatValue(property)
                            };
            if (property.MainSeparator != null)
                arguments.Add($"mainSeparator: {property.MainSeparator.Quote()}");
            if (property.KeyValueSeparator != null)
                arguments.Add($"keyValueSeparator: {property.KeyValueSeparator.Quote()}");
            if (property.Secret)
                arguments.Add($"secret: true");

            return $"  .Add({arguments.Join()})";
        }

        private static string GetPropertyFormatValue (Property property)
        {
            if (property.CustomValue)
                return $"Get{property.Name}()";
            if (property.Negate)
                return $"!{property.Name}";
            return property.Name;
        }
    }
}