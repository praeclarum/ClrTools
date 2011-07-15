using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Allocations
{
	class App
	{
		string asmPath = "";
		string typeName = "";
		string methodName = "";
		
		AssemblyDefinition asm;
		TypeDefinition type;
		MethodDefinition rootMethod;
		
		static TypeReference systemObject;
		
		SortedDictionary<string, MethodInfo> methodsToScan = new SortedDictionary<string, MethodInfo> ();
		SortedDictionary<string, MethodInfo> scannedMethods = new SortedDictionary<string, MethodInfo> ();
		
		void Run ()
		{
			LoadAssembly ();
			
			LoadType ();
			
			LoadMethod ();
			
			asm.MainModule.TryGetTypeReference ("System.Object", out systemObject);
			
			AddMethodToScan (rootMethod, null);
			
			while (methodsToScan.Count > 0) {
				var m = methodsToScan.First ();
				methodsToScan.Remove (m.Key);
				scannedMethods.Add (m.Key, m.Value);
				m.Value.Scan (AddMethodToScan);
			}
			
			foreach (var m in scannedMethods.Values) {
				m.Display (Console.Out);
			}
		}

		class MethodInfo
		{
			public MethodDefinition Def;
			public MethodInfo Caller;
			
			public enum AllocKind {
				NewObject,
				Box,
				NewArray,
			}
			
			public class Alloc {
				public TypeReference Type;
				public AllocKind Kind;
				public SequencePoint Point;
			}
			
			public List<Alloc> Allocations = new List<Alloc> ();
			
			public void Scan (Action<MethodReference, MethodInfo> markMethod)
			{
				foreach (var i in Def.Body.Instructions) {					
					switch (i.OpCode.Code) {
					case Code.Call:
						markMethod (i.Operand as MethodReference, this);
						break;
					case Code.Callvirt:
					case Code.Calli:
					{
						var m = (MethodReference)i.Operand;
						markMethod (m, this);
						var baseType = m.DeclaringType as TypeDefinition;
						if (baseType != null) {
							foreach (var t in baseType.Module.Types) {
								if (MethodsAreShared (t, baseType)) {
									var om = (from mm in t.Methods where mm.Name == m.Name && mm is MethodDefinition select (MethodDefinition)mm).FirstOrDefault();
									if (om != null) markMethod (om, this);
								}
							}
						}					
						
					}
						break;
					case Code.Box:
					{
						var t = i.Operand as TypeReference;
						if (t != null) {
							Allocations.Add (new Alloc { Type = t, Kind = AllocKind.Box, Point = GetSequencePoint (i) });
						}
					}
						break;
					case Code.Newarr:
					{
						var t = i.Operand as TypeReference;
						if (t != null) {
							Allocations.Add (new Alloc { Type = t, Kind = AllocKind.NewArray, Point = GetSequencePoint (i) });
						}
					}
						break;
					case Code.Newobj:
					{
						var m = i.Operand as MethodReference;
						if (m != null && !m.DeclaringType.IsValueType) {
							Allocations.Add (new Alloc { Type = m.DeclaringType, Kind = AllocKind.NewObject, Point = GetSequencePoint (i) });
						}
					}
						break;
					}
				}				
			}
			
			static SequencePoint GetSequencePoint (Instruction i)
			{
				var p = i;
				var pt = i.SequencePoint;
				while (pt == null && p != null) {
					pt = p.SequencePoint;
					p = p.Previous;
				}
				return pt;
			}

			static bool MethodsAreShared (TypeDefinition t, TypeDefinition baseType)
			{				
				if (t == baseType) return false;
				if (baseType.IsInterface) {
					return t.Interfaces.Contains (baseType);
				}
				else {
					var p = t;
					while (p != null && p != systemObject && p != baseType) {
						p = p.BaseType as TypeDefinition;
					}
					return p != null && p != systemObject;
				}
			}
			
			public void Display (TextWriter o)
			{
				if (Allocations.Count == 0) return;
				
				
				o.WriteLine (new string ('-', Def.FullName.Length));
				o.WriteLine (Def.FullName);
				var p = Caller;
				var t = "  ";
				while (p != null) {
					o.WriteLine (t + p.Def.FullName);
					t += "  ";
					p = p.Caller;
				}
				Console.ForegroundColor = ConsoleColor.DarkRed;
				foreach (var a in Allocations.OrderBy (k => k.Point != null ? k.Point.StartLine : 0)) {
					if (a.Point != null) {
						o.Write ("{0}:{1}: ", a.Point.Document.Url, a.Point.StartLine);
					}
					switch (a.Kind) {
					case AllocKind.NewObject:
						o.Write ("new ");
						break;
					case AllocKind.NewArray:
						o.Write ("new[] ");
						break;
					case AllocKind.Box:
						o.Write ("box ");
						break;
					}
					o.WriteLine (a.Type.FullName);
				}
				
				Console.ResetColor ();
			}
		}
		
		void AddMethodToScan (MethodReference m, MethodInfo caller)
		{
			if (m == null) return;
			if (m.DeclaringType.Namespace.StartsWith ("System")) return;
			var def = m as MethodDefinition;
			if (def == null) {
				try {
					def = m.Resolve ();
				}
				catch (Exception) {
					def = null;
				}
			}
			if (def != null && def.HasBody) {
				var key = def.FullName;
				if (scannedMethods.ContainsKey (key)) return;
				var mi = default(MethodInfo);				
				if (!methodsToScan.TryGetValue (key, out mi)) {
					
					mi = new MethodInfo () { Def = def, Caller = caller, };
					methodsToScan.Add (key, mi);
				}				
			}
		}
		
		void LoadMethod ()
		{
			rootMethod = (from m in type.Methods where m.Name == methodName select m).FirstOrDefault ();
			if (rootMethod == null) {
				throw new ApplicationException ("Could not find method `" + typeName + "` in type `" + type.FullName + "`");
			}
		}

		void LoadType ()
		{
			if (typeName.IndexOf ('.') >= 0) {
				type = asm.MainModule.GetType (typeName);
			}
			else {
				type = (from t in asm.MainModule.Types where t.Name == typeName select t).FirstOrDefault ();
			}
			if (type == null) {
				throw new ApplicationException ("Could not find the type `" + typeName + "`");
			}
		}

		void LoadAssembly ()
		{
			asm = AssemblyDefinition.ReadAssembly (asmPath, new ReaderParameters () {
				ReadSymbols = true,
			});
		}
		
		public static int Main (string[] args)
		{
			var app = new App ();
			
			if (args.Length == 3) {
				app.asmPath = Path.GetFullPath (args [0]);
				app.typeName = args [1];
				app.methodName = args [2];
				
				try {
					app.Run ();
					return 0;
				}
				catch (Exception error) {
					System.Console.WriteLine ("== ERROR ================");
					Console.Error.WriteLine (error);
					return 2;
				}
			}
			else {
				Console.WriteLine ("Allocations assembly class method");
				return 1;
			}
		}
	}
}
