using EciCas.Host;
using EciCas.Host.Startup;

// The console front end: the shared boot, then a prompt. The desktop shell
// calls the same HostBoot and follows it with a window instead.
var app = await HostBoot.StartAsync(args);

await ConsoleRepl.RunAsync(app);
