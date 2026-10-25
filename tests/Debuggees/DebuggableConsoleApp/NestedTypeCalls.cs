namespace DebuggableConsoleApp;

// Calls an evaluation makes to methods that take and return a type nested in another of the program's own.
public static class NestedTypeCalls
{
	public static MyClassContainingAnotherClass.MyNestedClass Nested(int value) => new() { NestedValue = value };

	public static int ValueOf(MyClassContainingAnotherClass.MyNestedClass nested) => nested.NestedValue;
}
