using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Markup;

namespace Passenger.WpfPreview.Design;

/// <summary>The designer's <c>d:DesignInstance</c>: an instance of the type when it is design-time creatable (its
/// parameterless constructor run), else a stand-in with the type's properties at their defaults, so bindings resolve
/// against its shape without running its code; a list of three when <see cref="CreateList"/>. The previewer reads a
/// <c>d:DataContext</c> with the designer's prefix mapped here.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class DesignInstanceExtension : MarkupExtension
{
    public DesignInstanceExtension()
    {
    }

    public DesignInstanceExtension(Type type) => Type = type;

    [ConstructorArgument("type")]
    public Type? Type { get; set; }

    public bool IsDesignTimeCreatable { get; set; }

    public bool CreateList { get; set; }

    public override object? ProvideValue(IServiceProvider serviceProvider)
    {
        if (Type is not { } type)
        {
            return null;
        }
        return CreateList ? new ObservableCollection<object> { One(type), One(type), One(type) } : One(type);
    }

    private object One(Type type)
    {
        if (IsDesignTimeCreatable && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
        {
            try
            {
                return Activator.CreateInstance(type)!;
            }
            catch (TargetInvocationException)
            {
                // Its constructor failed, as it may with no services behind it: the stand-in shows its shape still.
            }
        }
        return new DesignStandIn(type);
    }
}

/// <summary>A design-time stand-in for a type: its public properties, each at its type's default, as a binding sees an
/// object through its type descriptor.</summary>
public sealed class DesignStandIn : ICustomTypeDescriptor
{
    private readonly Type type;
    private readonly PropertyDescriptorCollection properties;

    public DesignStandIn(Type type)
    {
        this.type = type;
        properties = new PropertyDescriptorCollection([.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => new StandInProperty(g.First()))], readOnly: true);
    }

    public override string ToString() => type.Name;

    public AttributeCollection GetAttributes() => AttributeCollection.Empty;

    public string? GetClassName() => type.FullName;

    public string? GetComponentName() => null;

    public TypeConverter? GetConverter() => null;

    public EventDescriptor? GetDefaultEvent() => null;

    public PropertyDescriptor? GetDefaultProperty() => null;

    public object? GetEditor(Type editorBaseType) => null;

    public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;

    public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;

    public PropertyDescriptorCollection GetProperties() => properties;

    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => properties;

    public object? GetPropertyOwner(PropertyDescriptor? pd) => this;

    private sealed class StandInProperty(PropertyInfo property) : PropertyDescriptor(property.Name, null)
    {
        private readonly object? value = property.PropertyType.IsValueType && !property.PropertyType.ContainsGenericParameters
            ? Activator.CreateInstance(property.PropertyType)
            : null;

        public override Type ComponentType => typeof(DesignStandIn);

        public override bool IsReadOnly => true;

        public override Type PropertyType => property.PropertyType;

        public override bool CanResetValue(object component) => false;

        public override object? GetValue(object? component) => value;

        public override void ResetValue(object component)
        {
        }

        public override void SetValue(object? component, object? value)
        {
        }

        public override bool ShouldSerializeValue(object component) => false;
    }
}
