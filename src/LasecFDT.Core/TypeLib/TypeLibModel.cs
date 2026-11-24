using System.Collections.Generic;

namespace LasecFDT.Core.TypeLib
{
    /// <summary>Descrição de uma type library COM, independente de COM (serializável).</summary>
    public sealed class TypeLibModel
    {
        public string Name { get; set; }
        public string Doc { get; set; }
        public string LibId { get; set; }
        public string Version { get; set; }
        public string SourcePath { get; set; }
        public List<TypeModel> Types { get; set; } = new List<TypeModel>();
    }

    public sealed class TypeModel
    {
        public string Name { get; set; }
        public string Doc { get; set; }
        public string Guid { get; set; }
        /// <summary>DISPATCH, INTERFACE, COCLASS, ALIAS, ENUM, RECORD, MODULE, UNION.</summary>
        public string Kind { get; set; }
        public int Flags { get; set; }
        public bool IsDual { get; set; }
        public bool IsHidden { get; set; }
        public string BaseInterface { get; set; }
        public string AliasOf { get; set; }
        public List<MethodModel> Methods { get; set; } = new List<MethodModel>();
        public List<ImplementedInterface> Implements { get; set; } = new List<ImplementedInterface>();
        public List<MemberModel> Members { get; set; } = new List<MemberModel>();
    }

    public sealed class MethodModel
    {
        public string Name { get; set; }
        public int DispId { get; set; }
        public string InvokeKind { get; set; }
        public string ReturnType { get; set; }
        public int VtableOffset { get; set; }
        public List<ParameterModel> Parameters { get; set; } = new List<ParameterModel>();
    }

    public sealed class ParameterModel
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool In { get; set; }
        public bool Out { get; set; }
        public bool RetVal { get; set; }
    }

    public sealed class ImplementedInterface
    {
        public string Name { get; set; }
        public string Iid { get; set; }
        public bool IsDefault { get; set; }
        public bool IsSource { get; set; }
    }

    public sealed class MemberModel
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
    }
}
