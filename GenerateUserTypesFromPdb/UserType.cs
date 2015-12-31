﻿using Dia2Lib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GenerateUserTypesFromPdb
{
    [Flags]
    enum UserTypeGenerationFlags
    {
        None = 0,
        SingleLineProperty,
        GenerateFieldTypeInfoComment,
        UseClassFieldsFromDiaSymbolProvider,
    }

    class UserTypeField
    {
        public string FieldName { get; set; }

        public string FieldType { get; set; }

        public string PropertyName { get; set; }

        public string ConstructorText { get; set; }

        public string FieldTypeInfoComment { get; set; }

        public bool Static { get; set; }
    }

    class UserType
    {
        public UserType(IDiaSymbol symbol, XmlType xmlType, string moduleName)
        {
            Symbol = symbol;
            XmlType = xmlType;
            ModuleName = moduleName;
            InnerTypes = new List<UserType>();
            Usings = new HashSet<string>(new string[] { "CsScripts" });
        }

        public IDiaSymbol Symbol { get; private set; }

        public string ModuleName { get; private set; }

        public string Namespace { get; set; }

        public UserType DeclaredInType { get; private set; }

        public List<UserType> InnerTypes { get; private set; }

        public HashSet<string> Usings { get; private set; }

        public XmlType XmlType { get; private set; }

        public string ClassName
        {
            get
            {
                if (DeclaredInType != null)
                {
                    return Symbol.name.Substring(DeclaredInType.Symbol.name.Length + 2);
                }

                return Symbol.name;
            }
        }

        public string FullClassName
        {
            get
            {
                if (DeclaredInType != null)
                {
                    return string.Format("{0}.{1}", DeclaredInType.FullClassName, ClassName);
                }

                if (Namespace != null)
                {
                    return string.Format("{0}.{1}", Namespace, ClassName);
                }

                return ClassName;
            }
        }

        private IEnumerable<UserTypeField> ExtractFields(Dictionary<string, UserType> exportedTypes, IEnumerable<XmlTypeTransformation> typeTransformations, UserTypeGenerationFlags options)
        {
            var symbol = Symbol;
            var moduleName = ModuleName;
            var fields = symbol.GetChildren(SymTagEnum.SymTagData);
            bool hasNonStatic = false;
            bool useThisClass = options.HasFlag(UserTypeGenerationFlags.UseClassFieldsFromDiaSymbolProvider);

            foreach (var field in fields)
            {
                if (IsFieldFiltered(field))
                    continue;

                bool isStatic = (DataKind)field.dataKind == DataKind.StaticMember;
                string originalFieldTypeString = TypeToString.GetTypeString(field.type);
                string fieldTypeString = GetTypeString(field.type, exportedTypes, field.length);
                string castingTypeString = GetCastingType(fieldTypeString);
                string fieldName = field.name;
                string simpleFieldValue;
                string constructorText;

                if (isStatic)
                {
                    simpleFieldValue = string.Format("Process.Current.GetGlobal(\"{0}!{1}::{2}\")", moduleName, symbol.name, field.name);
                }
                else
                {
                    string gettingField = "variable.GetField";

                    if (options.HasFlag(UserTypeGenerationFlags.UseClassFieldsFromDiaSymbolProvider))
                    {
                        gettingField = "thisClass.GetClassField";
                    }

                    simpleFieldValue = string.Format("{0}(\"{1}\")", gettingField, field.name);
                    hasNonStatic = true;
                }

                if (castingTypeString.StartsWith("BasicType<"))
                {
                    constructorText = string.Format("{0}.GetValue({1})", castingTypeString, simpleFieldValue);
                }
                else if (string.IsNullOrEmpty(castingTypeString))
                {
                    constructorText = simpleFieldValue;
                }
                else if (castingTypeString == "NakedPointer" || castingTypeString == "CodeFunction" || castingTypeString.StartsWith("CodeArray") || castingTypeString.StartsWith("CodePointer"))
                {
                    constructorText = string.Format("new {0}({1})", castingTypeString, simpleFieldValue);
                }
                else
                {
                    constructorText = string.Format("({0}){1}", castingTypeString, simpleFieldValue);
                }

                var transformation = typeTransformations.Where(t => t.Matches(originalFieldTypeString)).FirstOrDefault();

                if (transformation != null)
                {
                    Func<string, string> typeConverter = null;

                    typeConverter = (inputType) =>
                    {
                        UserType userType;

                        if (exportedTypes.TryGetValue(inputType, out userType))
                        {
                            return userType.FullClassName;
                        }

                        var tr = typeTransformations.Where(t => t.Matches(inputType)).FirstOrDefault();

                        if (tr != null)
                        {
                            return tr.TransformType(inputType, ClassName, typeConverter);
                        }

                        return "Variable";
                    };
                    string newFieldTypeString = transformation.TransformType(originalFieldTypeString, ClassName, typeConverter);
                    string fieldOffset = string.Format("{0}.GetFieldOffset(\"{1}\")", useThisClass ? "variable" : "thisClass", field.name);

                    if (isStatic)
                    {
                        fieldOffset = "<Field offset was used on a static member>";
                    }

                    constructorText = transformation.TransformConstructor(originalFieldTypeString, simpleFieldValue, fieldOffset, ClassName, typeConverter);
                    fieldTypeString = newFieldTypeString;
                }

                yield return new UserTypeField
                {
                    ConstructorText = constructorText,
                    FieldName = "_" + fieldName,
                    FieldType = fieldTypeString,
                    FieldTypeInfoComment = string.Format("// {0} {1};", TypeToString.GetTypeString(field.type), field.name),
                    PropertyName = fieldName,
                    Static = isStatic,
                };
            }

            if (hasNonStatic && useThisClass)
            {
                yield return new UserTypeField
                {
                    ConstructorText = string.Format("variable.GetBaseClass(\"{0}\")", symbol.name),
                    FieldName = "thisClass",
                    FieldType = "Variable",
                    FieldTypeInfoComment = null,
                    PropertyName = null,
                    Static = false,
                };
            }
        }

        public void WriteCode(IndentedWriter output, TextWriter error, Dictionary<string, UserType> exportedTypes, IEnumerable<XmlTypeTransformation> typeTransformations, UserTypeGenerationFlags options, int indentation = 0)
        {
            var symbol = Symbol;
            var moduleName = ModuleName;
            var fields = ExtractFields(exportedTypes, typeTransformations, options).OrderBy(f => !f.Static).ThenBy(f => f.FieldName).ToArray();
            bool hasStatic = fields.Where(f => f.Static).Any(), hasNonStatic = fields.Where(f => !f.Static).Any();
            string baseType = GetBaseTypeString(error, symbol, exportedTypes);

            if (DeclaredInType == null)
            {
                if (Usings.Count > 0)
                {
                    foreach (var u in Usings.OrderBy(s => s))
                    {
                        output.WriteLine(indentation, "using {0};", u);
                    }

                    output.WriteLine();
                }

                if (!string.IsNullOrEmpty(Namespace))
                {
                    output.WriteLine(indentation, "namespace {0}", Namespace);
                    output.WriteLine(indentation++, "{{");
                }
            }

            output.WriteLine(indentation, @"[UserType(ModuleName = ""{0}"", TypeName = ""{1}"")]", moduleName, symbol.name);
            output.WriteLine(indentation, @"public partial class {0} : {1}", ClassName, baseType);
            output.WriteLine(indentation++, @"{{");

            foreach (var field in fields)
            {
                if (options.HasFlag(UserTypeGenerationFlags.GenerateFieldTypeInfoComment) && !string.IsNullOrEmpty(field.FieldTypeInfoComment))
                    output.WriteLine(indentation, field.FieldTypeInfoComment);
                output.WriteLine(indentation, "private {0}UserMember<{1}> {2};", field.Static ? "static " : "", field.FieldType, field.FieldName);
                if (options.HasFlag(UserTypeGenerationFlags.GenerateFieldTypeInfoComment))
                    output.WriteLine();
            }

            if (hasStatic)
            {
                output.WriteLine();
                output.WriteLine(indentation, "static {0}()", ClassName);
                output.WriteLine(indentation++, "{{");

                foreach (var field in fields)
                {
                    if (field.Static)
                    {
                        output.WriteLine(indentation, "{0} = UserMember.Create(() => {1});", field.FieldName, field.ConstructorText);
                    }
                }

                output.WriteLine(--indentation, "}}");
            }

            if (hasNonStatic)
            {
                output.WriteLine();
                output.WriteLine(indentation, "public {0}(Variable variable)", ClassName);
                output.WriteLine(indentation + 1, ": base(variable)");
                output.WriteLine(indentation++, "{{");

                foreach (var field in fields)
                {
                    if (!field.Static)
                    {
                        output.WriteLine(indentation, "{0} = UserMember.Create(() => {1});", field.FieldName, field.ConstructorText);
                    }
                }

                output.WriteLine(--indentation, "}}");
            }

            bool firstField = true;
            foreach (var field in fields)
            {
                if (string.IsNullOrEmpty(field.PropertyName))
                {
                    continue;
                }

                if (options.HasFlag(UserTypeGenerationFlags.SingleLineProperty))
                {
                    if (firstField)
                    {
                        output.WriteLine();
                        firstField = false;
                    }

                    output.WriteLine(indentation, "public {0}{1} {2} {{ get {{ return {3}.Value; }} }}", field.Static ? "static " : "", field.FieldType, field.PropertyName, field.FieldName);
                }
                else
                {
                    output.WriteLine();
                    output.WriteLine(indentation, "public {0}{1} {2}", field.Static ? "static " : "", field.FieldType, field.PropertyName);
                    output.WriteLine(indentation++, "{{");
                    output.WriteLine(indentation, "get");
                    output.WriteLine(indentation++, "{{");
                    output.WriteLine(indentation, "return {0}.Value;", field.FieldName);
                    output.WriteLine(--indentation, "}}");
                    output.WriteLine(--indentation, "}}");
                }
            }

            foreach (var innerType in InnerTypes)
            {
                output.WriteLine();
                innerType.WriteCode(output, error, exportedTypes, typeTransformations, options, indentation);
            }

            output.WriteLine(--indentation, @"}}");

            if (DeclaredInType == null && !string.IsNullOrEmpty(Namespace))
            {
                output.WriteLine(--indentation, "}}");
            }
        }

        private bool IsFieldFiltered(IDiaSymbol field)
        {
            return (XmlType.IncludedFields.Count > 0 && !XmlType.IncludedFields.Contains(field.name))
                || XmlType.ExcludedFields.Contains(field.name);
        }

        public void SetDeclaredInType(UserType declaredInType)
        {
            DeclaredInType = declaredInType;
            declaredInType.InnerTypes.Add(this);
            foreach (var u in Usings)
            {
                declaredInType.Usings.Add(u);
            }
        }

        private static string GetCastingType(string typeString)
        {
            if (typeString.EndsWith("?"))
                return "BasicType<" + typeString.Substring(0, typeString.Length - 1) + ">";
            if (typeString == "Variable")
                return "";
            return typeString;
        }

        private static string GetBaseTypeString(TextWriter error, IDiaSymbol type, Dictionary<string, UserType> exportedTypes)
        {
            var baseClasses = type.GetBaseClasses().ToArray();

            if (baseClasses.Length > 1)
            {
                //throw new Exception(string.Format("Multiple inheritance is not supported. Type {0} is inherited from {1}", type.name, string.Join(", ", baseClasses.Select(c => c.name))));
                error.WriteLine(string.Format("Multiple inheritance is not supported, defaulting to 'UserType' as base class. Type {0} is inherited from:\n  {1}", type.name, string.Join("\n  ", baseClasses.Select(c => c.name))));
                return "UserType";
            }

            if (baseClasses.Length == 1)
            {
                type = baseClasses[0];

                UserType userType;

                if (exportedTypes.TryGetValue(type.name, out userType))
                {
                    return userType.FullClassName;
                }

                return GetBaseTypeString(error, type, exportedTypes);
            }

            return "UserType";
        }

        private static string GetTypeString(IDiaSymbol type, Dictionary<string, UserType> exportedTypes, ulong bitLength = 0)
        {
            switch ((SymTagEnum)type.symTag)
            {
                case SymTagEnum.SymTagBaseType:
                    if (bitLength == 1)
                        return "bool";
                    switch ((BasicType)type.baseType)
                    {
                        case BasicType.Bit:
                        case BasicType.Bool:
                            return "bool";
                        case BasicType.Char:
                        case BasicType.WChar:
                            return "char";
                        case BasicType.BSTR:
                            return "string";
                        case BasicType.Void:
                            return "void";
                        case BasicType.Float:
                            return type.length <= 4 ? "float" : "double";
                        case BasicType.Int:
                        case BasicType.Long:
                            switch (type.length)
                            {
                                case 0:
                                    return "void";
                                case 1:
                                    return "sbyte";
                                case 2:
                                    return "short";
                                case 4:
                                    return "int";
                                case 8:
                                    return "long";
                                default:
                                    throw new Exception("Unexpected type length " + type.length);
                            }

                        case BasicType.UInt:
                        case BasicType.ULong:
                            switch (type.length)
                            {
                                case 0:
                                    return "void";
                                case 1:
                                    return "byte";
                                case 2:
                                    return "ushort";
                                case 4:
                                    return "uint";
                                case 8:
                                    return "ulong";
                                default:
                                    throw new Exception("Unexpected type length " + type.length);
                            }

                        case BasicType.Hresult:
                            return "Hresult";
                        default:
                            throw new Exception("Unexpected basic type " + (BasicType)type.baseType);
                    }

                case SymTagEnum.SymTagPointerType:
                    {
                        IDiaSymbol pointerType = type.type;

                        switch ((SymTagEnum)pointerType.symTag)
                        {
                            case SymTagEnum.SymTagBaseType:
                            case SymTagEnum.SymTagEnum:
                                {
                                    string innerType = GetTypeString(pointerType, exportedTypes);

                                    if (innerType == "void")
                                        return "NakedPointer";
                                    if (innerType == "char")
                                        return "string";
                                    return innerType + "?";
                                }

                            case SymTagEnum.SymTagUDT:
                                return GetTypeString(pointerType, exportedTypes);
                            default:
                                return "CodePointer<" + GetTypeString(pointerType, exportedTypes) + ">";
                        }
                    }

                case SymTagEnum.SymTagUDT:
                case SymTagEnum.SymTagEnum:
                    {
                        string typeName = type.name;
                        UserType userType;

                        if (exportedTypes.TryGetValue(typeName, out userType))
                        {
                            return userType.FullClassName;
                        }

                        if ((SymTagEnum)type.symTag == SymTagEnum.SymTagEnum)
                        {
                            return "uint";
                        }

                        return "Variable";
                    }

                case SymTagEnum.SymTagArrayType:
                    return "CodeArray<" + GetTypeString(type.type, exportedTypes) + ">";

                case SymTagEnum.SymTagFunctionType:
                    return "CodeFunction";

                default:
                    throw new Exception("Unexpected type tag " + (SymTagEnum)type.symTag);
            }
        }
    }
}