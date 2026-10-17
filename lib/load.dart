/// A load harness: a host and many scripted clients on one real tick clock.
///
/// The clients run on the same thread as the host. Input handling and the
/// clients' own work happen between ticks, outside every phase, and show up
/// as drift. The phase table is only the host's publish.
library;

export 'src/load.dart'
    show LoadOptions, LoadReport, LoadScript, loadScripts, runLoad;
export 'src/load_args.dart' show loadUsage, parseLoadArgs;
