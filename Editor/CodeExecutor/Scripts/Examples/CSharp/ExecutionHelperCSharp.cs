#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using System.Reflection;
using System.CodeDom.Compiler;
using Microsoft.CSharp;

namespace ChenPipi.CodeExecutor.Examples
{

    /// <summary>
    /// CodeExecutor C# 执行工具
    /// </summary>
    public static class ExecutionHelperCSharp
    {

        /// <summary>
        /// 执行信息
        /// </summary>
        public struct ExecutionInfo
        {
            public string namespaceName;
            public string className;
            public string methodName;
        }

        /// <summary>
        /// 默认执行信息
        /// </summary>
        private static readonly ExecutionInfo s_DefaultExecutionInfo = new ExecutionInfo()
        {
            namespaceName = "WrapperNamespace",
            className = "WrapperClass",
            methodName = "WrapperMethod",
        };

        /// <summary>
        /// 执行代码段
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static object[] ExecuteSnippetCode(string code)
        {
            // 用临时的命名空间和类包裹代码段
            string wrappedCode = WrapCodeSnippet(code, s_DefaultExecutionInfo);
            Debug.Log($"[CodeExecutor] Wrapped code:\r\n{wrappedCode}");
            // 执行
            return ExecuteCode(wrappedCode, s_DefaultExecutionInfo);
        }

        /// <summary>
        /// 执行代码
        /// </summary>
        /// <param name="code"></param>
        /// <param name="executionInfo"></param>
        /// <returns></returns>
        public static object[] ExecuteCode(string code, ExecutionInfo executionInfo)
        {
            // 编译代码
            Assembly assembly = CompileCode(code);
            if (assembly == null)
            {
                return null;
            }

            // 获取包装类型
            string typeName = new string[] { executionInfo.namespaceName, executionInfo.className }.Join(".", true);
            object tempClassInstance = assembly.CreateInstance(typeName);
            if (tempClassInstance == null)
            {
                Debug.LogError($"[CodeExecutor] Cannot find class \'{typeName}\'");
                return null;
            }

            // 获取包装函数
            MethodInfo executeMethodInfo = tempClassInstance!.GetType().GetMethod(executionInfo.methodName);
            if (executeMethodInfo == null)
            {
                Debug.LogError($"[CodeExecutor] Cannot find method \'{executionInfo.methodName}\'");
                return null;
            }

            // 执行
            object result = executeMethodInfo!.Invoke(tempClassInstance, null);

            // 打印结果
            if (result != null)
            {
                Debug.Log($"[CodeExecutor] Code execution results: <color=#00FF00>{result}</color>\n");
            }

            // 返回结果
            return new[] { result };
        }

        /// <summary>
        /// 使用命名空间和类包裹代码段
        /// </summary>
        /// <param name="code"></param>
        /// <param name="executionInfo"></param>
        /// <returns></returns>
        private static string WrapCodeSnippet(string code, ExecutionInfo executionInfo)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.Append("using System;\r\n");
            stringBuilder.Append("using UnityEngine;\r\n\r\n");

            if (!string.IsNullOrEmpty(executionInfo.namespaceName))
            {
                stringBuilder.Append($"namespace {executionInfo.namespaceName}\r\n");
                stringBuilder.Append("{\r\n");
            }

            stringBuilder.Append($"      public class {executionInfo.className}\r\n");
            stringBuilder.Append("      {\r\n");
            stringBuilder.Append($"          public object {executionInfo.methodName}()\r\n");
            stringBuilder.Append("          {\r\n");
            stringBuilder.Append("#pragma warning disable 0162\r\n"); // Avoid "Unreachable code detected"

            stringBuilder.Append($"              // ---------- Here are your codes ---------- \r\n");
            string[] lines = code.Split(';');
            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                stringBuilder.Append($"              {line.Replace("\n", "")};\r\n");
            }
            stringBuilder.Append($"              // ---------- Here are your codes ---------- \r\n");

            stringBuilder.Append("              return null;\r\n");
            stringBuilder.Append("#pragma warning restore 0162\r\n");
            stringBuilder.Append("          }\r\n");
            stringBuilder.Append("      }\r\n");

            if (!string.IsNullOrEmpty(executionInfo.namespaceName))
            {
                stringBuilder.Append("}\r\n");
            }

            return stringBuilder.ToString();
        }

        /// <summary>
        /// 编译代码
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static Assembly CompileCode(string code)
        {
            CSharpCodeProvider provider = new CSharpCodeProvider();

            CompilerParameters parameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
            };

            string unityManaged =
                Path.Combine(UnityEditor.EditorApplication.applicationContentsPath, "Managed");

            string unityEngine =
                Path.Combine(unityManaged, "UnityEngine");

            string scriptAssemblies =
                Path.Combine(
                    Directory.GetParent(Application.dataPath).FullName,
                    "Library/ScriptAssemblies");

            string netstandardDir =
                Path.Combine(UnityEditor.EditorApplication.applicationContentsPath, "NetStandard/ref/2.1.0");

            void AddIfExists(string path)
            {
                if (File.Exists(path))
                    parameters.ReferencedAssemblies.Add(path);
            }

            // NET STANDARD
            AddIfExists(Path.Combine(netstandardDir, "netstandard.dll"));
            AddIfExists(Path.Combine(netstandardDir, "System.Runtime.dll"));
            AddIfExists(Path.Combine(netstandardDir, "System.Collections.dll"));
            AddIfExists(Path.Combine(netstandardDir, "System.Linq.dll"));

            // System
            AddIfExists(Path.Combine(unityManaged, "System.dll"));
            AddIfExists(Path.Combine(unityManaged, "System.Core.dll"));
            AddIfExists(Path.Combine(unityManaged, "mscorlib.dll"));

            // Unity
            AddIfExists(Path.Combine(unityEngine, "UnityEngine.dll"));
            AddIfExists(Path.Combine(unityEngine, "UnityEngine.CoreModule.dll"));

            // Editor
            AddIfExists(Path.Combine(unityManaged, "UnityEditor.dll"));

            // Project
            AddIfExists(Path.Combine(scriptAssemblies, "Assembly-CSharp.dll"));

            CompilerResults results =
                provider.CompileAssemblyFromSource(parameters, code);

            if (results.Errors.HasErrors)
            {
                foreach (CompilerError err in results.Errors)
                {
                    Debug.LogError(err.ToString());
                }

                return null;
            }

            return results.CompiledAssembly;
        }

#region List Extension

        private static string Join<T>(this IList<T> list, string separator = "", bool ignoreEmptyOrNull = true)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < list.Count; ++i)
            {
                if (ignoreEmptyOrNull && string.IsNullOrEmpty(list[i].ToString()))
                {
                    continue;
                }
                builder.Append(list[i]);
                if (i < list.Count - 1)
                {
                    builder.Append(separator);
                }
            }
            return builder.ToString();
        }

#endregion

    }

}

#endif
