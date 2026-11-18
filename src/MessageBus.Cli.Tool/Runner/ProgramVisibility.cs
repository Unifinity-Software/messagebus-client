// The ASP.NET Core framework (pulled in by the runner connection, the original issue) makes a top-level Program
// public by default; keep mbus's internal so projects that reference both mbus and the API build.
internal partial class Program;
