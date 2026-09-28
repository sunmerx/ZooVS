// ZooVS host (generated)
"use strict";
var __create = Object.create;
var __defProp = Object.defineProperty;
var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
var __getOwnPropNames = Object.getOwnPropertyNames;
var __getProtoOf = Object.getPrototypeOf;
var __hasOwnProp = Object.prototype.hasOwnProperty;
var __copyProps = (to, from, except, desc) => {
  if (from && typeof from === "object" || typeof from === "function") {
    for (let key of __getOwnPropNames(from))
      if (!__hasOwnProp.call(to, key) && key !== except)
        __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
  }
  return to;
};
var __toESM = (mod, isNodeMode, target) => (target = mod != null ? __create(__getProtoOf(mod)) : {}, __copyProps(
  // If the importer is in node compatibility mode or this is not an ESM
  // file that has been converted to a CommonJS file using a Babel-
  // compatible transform (i.e. "__esModule" has not been set), then set
  // "default" to the CommonJS "module.exports" for node compatibility.
  isNodeMode || !mod || !mod.__esModule ? __defProp(target, "default", { value: mod, enumerable: true }) : target,
  mod
));

// zoohost/src/host.ts
var import_module = require("module");
var import_fs = __toESM(require("fs"));
var import_path = __toESM(require("path"));
var import_readline = __toESM(require("readline"));
var import_events = require("events");

// packages/vscode-shim/src/classes/Position.ts
var Position = class _Position {
  /**
   * The zero-based line number
   */
  line;
  /**
   * The zero-based character offset
   */
  character;
  /**
   * Create a new Position
   *
   * @param line - The zero-based line number
   * @param character - The zero-based character offset
   */
  constructor(line, character) {
    if (line < 0) {
      throw new Error("Line number must be non-negative");
    }
    if (character < 0) {
      throw new Error("Character offset must be non-negative");
    }
    this.line = line;
    this.character = character;
  }
  /**
   * Check if this position is equal to another position
   */
  isEqual(other) {
    return this.line === other.line && this.character === other.character;
  }
  /**
   * Check if this position is before another position
   */
  isBefore(other) {
    if (this.line < other.line) {
      return true;
    }
    if (this.line === other.line) {
      return this.character < other.character;
    }
    return false;
  }
  /**
   * Check if this position is before or equal to another position
   */
  isBeforeOrEqual(other) {
    return this.isBefore(other) || this.isEqual(other);
  }
  /**
   * Check if this position is after another position
   */
  isAfter(other) {
    return !this.isBeforeOrEqual(other);
  }
  /**
   * Check if this position is after or equal to another position
   */
  isAfterOrEqual(other) {
    return !this.isBefore(other);
  }
  /**
   * Compare this position to another
   *
   * @returns -1 if this position is before, 0 if equal, 1 if after
   */
  compareTo(other) {
    if (this.line < other.line) {
      return -1;
    }
    if (this.line > other.line) {
      return 1;
    }
    if (this.character < other.character) {
      return -1;
    }
    if (this.character > other.character) {
      return 1;
    }
    return 0;
  }
  translate(lineDeltaOrChange, characterDelta) {
    if (typeof lineDeltaOrChange === "object") {
      return new _Position(
        this.line + (lineDeltaOrChange.lineDelta || 0),
        this.character + (lineDeltaOrChange.characterDelta || 0)
      );
    }
    return new _Position(this.line + (lineDeltaOrChange || 0), this.character + (characterDelta || 0));
  }
  with(lineOrChange, character) {
    if (typeof lineOrChange === "object") {
      return new _Position(
        lineOrChange.line !== void 0 ? lineOrChange.line : this.line,
        lineOrChange.character !== void 0 ? lineOrChange.character : this.character
      );
    }
    return new _Position(
      lineOrChange !== void 0 ? lineOrChange : this.line,
      character !== void 0 ? character : this.character
    );
  }
};

// packages/vscode-shim/src/classes/Range.ts
var Range = class _Range {
  start;
  end;
  constructor(startOrStartLine, endOrStartCharacter, endLine, endCharacter) {
    if (typeof startOrStartLine === "number") {
      this.start = new Position(startOrStartLine, endOrStartCharacter);
      this.end = new Position(endLine, endCharacter);
    } else {
      this.start = startOrStartLine;
      this.end = endOrStartCharacter;
    }
  }
  /**
   * Check if this range is empty (start equals end)
   */
  get isEmpty() {
    return this.start.isEqual(this.end);
  }
  /**
   * Check if this range is on a single line
   */
  get isSingleLine() {
    return this.start.line === this.end.line;
  }
  /**
   * Check if this range contains a position or range
   *
   * @param positionOrRange - The position or range to check
   * @returns true if the position/range is within this range
   */
  contains(positionOrRange) {
    if ("start" in positionOrRange && "end" in positionOrRange) {
      return this.contains(positionOrRange.start) && this.contains(positionOrRange.end);
    }
    return positionOrRange.isAfterOrEqual(this.start) && positionOrRange.isBeforeOrEqual(this.end);
  }
  /**
   * Check if this range is equal to another range
   */
  isEqual(other) {
    return this.start.isEqual(other.start) && this.end.isEqual(other.end);
  }
  /**
   * Get the intersection of this range with another range
   *
   * @param other - The other range
   * @returns The intersection range, or undefined if they don't intersect
   */
  intersection(other) {
    const start = this.start.isAfter(other.start) ? this.start : other.start;
    const end = this.end.isBefore(other.end) ? this.end : other.end;
    if (start.isAfter(end)) {
      return void 0;
    }
    return new _Range(start, end);
  }
  /**
   * Get the union of this range with another range
   *
   * @param other - The other range
   * @returns A new range that spans both ranges
   */
  union(other) {
    const start = this.start.isBefore(other.start) ? this.start : other.start;
    const end = this.end.isAfter(other.end) ? this.end : other.end;
    return new _Range(start, end);
  }
  with(startOrChange, end) {
    if (startOrChange && typeof startOrChange === "object" && !("line" in startOrChange)) {
      const change = startOrChange;
      return new _Range(change.start || this.start, change.end || this.end);
    }
    return new _Range(startOrChange || this.start, end || this.end);
  }
};

// packages/vscode-shim/src/classes/Selection.ts
var Selection = class extends Range {
  /**
   * The anchor position (where the selection started)
   */
  anchor;
  /**
   * The active position (where the selection currently ends)
   */
  active;
  constructor(anchorOrAnchorLine, activeOrAnchorCharacter, activeLine, activeCharacter) {
    let anchor;
    let active;
    if (typeof anchorOrAnchorLine === "number") {
      anchor = new Position(anchorOrAnchorLine, activeOrAnchorCharacter);
      active = new Position(activeLine, activeCharacter);
    } else {
      anchor = anchorOrAnchorLine;
      active = activeOrAnchorCharacter;
    }
    super(anchor, active);
    this.anchor = anchor;
    this.active = active;
  }
  /**
   * Check if the selection is reversed
   * A reversed selection has the active position before the anchor position
   */
  get isReversed() {
    return this.anchor.isAfter(this.active);
  }
};

// packages/vscode-shim/src/classes/Uri.ts
var path = __toESM(require("path"), 1);
var Uri = class _Uri {
  scheme;
  authority;
  path;
  query;
  fragment;
  constructor(scheme, authority, path11, query, fragment) {
    this.scheme = scheme;
    this.authority = authority;
    this.path = path11;
    this.query = query;
    this.fragment = fragment;
  }
  /**
   * Create a URI from a file system path
   *
   * @param path - The file system path
   * @returns A new Uri instance with 'file' scheme
   */
  static file(fsPath) {
    return new _Uri("file", "", fsPath, "", "");
  }
  /**
   * Parse a URI string
   *
   * @param value - The URI string to parse
   * @returns A new Uri instance
   */
  static parse(value) {
    try {
      const url = new URL(value);
      return new _Uri(
        url.protocol.slice(0, -1),
        url.hostname,
        url.pathname,
        url.search.slice(1),
        url.hash.slice(1)
      );
    } catch {
      return _Uri.file(value);
    }
  }
  /**
   * Join a URI with path segments
   *
   * @param base - The base URI
   * @param pathSegments - Path segments to join
   * @returns A new Uri with the joined path
   */
  static joinPath(base, ...pathSegments) {
    const joinedPath = path.join(base.path, ...pathSegments);
    return new _Uri(base.scheme, base.authority, joinedPath, base.query, base.fragment);
  }
  /**
   * Create a new URI with modifications
   *
   * @param change - The changes to apply
   * @returns A new Uri instance with the changes applied
   */
  with(change) {
    return new _Uri(
      change.scheme !== void 0 ? change.scheme : this.scheme,
      change.authority !== void 0 ? change.authority : this.authority,
      change.path !== void 0 ? change.path : this.path,
      change.query !== void 0 ? change.query : this.query,
      change.fragment !== void 0 ? change.fragment : this.fragment
    );
  }
  /**
   * Get the file system path representation
   * Compatible with both Unix and Windows paths
   */
  get fsPath() {
    return this.path;
  }
  /**
   * Convert the URI to a string representation
   */
  toString() {
    return `${this.scheme}://${this.authority}${this.path}${this.query ? "?" + this.query : ""}${this.fragment ? "#" + this.fragment : ""}`;
  }
  /**
   * Convert to JSON representation
   */
  toJSON() {
    return {
      scheme: this.scheme,
      authority: this.authority,
      path: this.path,
      query: this.query,
      fragment: this.fragment
    };
  }
};

// packages/vscode-shim/src/classes/EventEmitter.ts
var EventEmitter = class {
  #listeners = /* @__PURE__ */ new Set();
  /**
   * The event that listeners can subscribe to
   *
   * @param listener - The callback function to invoke when the event fires
   * @param thisArgs - Optional 'this' context for the listener
   * @param disposables - Optional array to add the disposable to
   * @returns A disposable to unsubscribe from the event
   */
  event = (listener, thisArgs, disposables) => {
    const fn = thisArgs ? listener.bind(thisArgs) : listener;
    this.#listeners.add(fn);
    const disposable = {
      dispose: () => {
        this.#listeners.delete(fn);
      }
    };
    if (disposables) {
      disposables.push(disposable);
    }
    return disposable;
  };
  /**
   * Fire the event, notifying all subscribers
   *
   * Failure of one or more listeners will not fail this function call.
   * Failed listeners will be caught and ignored to prevent one listener
   * from breaking others.
   *
   * @param data - The event data to pass to listeners
   */
  fire(data) {
    for (const listener of this.#listeners) {
      try {
        listener(data);
      } catch (error) {
        console.error("EventEmitter listener error:", error);
      }
    }
  }
  /**
   * Dispose this event emitter and remove all listeners
   */
  dispose() {
    this.#listeners.clear();
  }
  /**
   * Get the current number of listeners (useful for debugging)
   */
  get listenerCount() {
    return this.#listeners.size;
  }
};

// packages/vscode-shim/src/classes/TextEdit.ts
var TextEdit = class _TextEdit {
  /**
   * The range to replace
   */
  range;
  /**
   * The new text (empty string for deletion)
   */
  newText;
  /**
   * Create a new TextEdit
   *
   * @param range - The range to replace
   * @param newText - The new text
   */
  constructor(range, newText) {
    this.range = range;
    this.newText = newText;
  }
  /**
   * Create a replace edit
   *
   * @param range - The range to replace
   * @param newText - The new text
   * @returns A new TextEdit
   */
  static replace(range, newText) {
    return new _TextEdit(range, newText);
  }
  /**
   * Create an insert edit
   *
   * @param position - The position to insert at
   * @param newText - The text to insert
   * @returns A new TextEdit
   */
  static insert(position, newText) {
    return new _TextEdit(new Range(position, position), newText);
  }
  /**
   * Create a delete edit
   *
   * @param range - The range to delete
   * @returns A new TextEdit
   */
  static delete(range) {
    return new _TextEdit(range, "");
  }
  /**
   * Create an edit to set the end of line sequence
   *
   * @returns A new TextEdit (simplified implementation)
   */
  static setEndOfLine() {
    return new _TextEdit(new Range(new Position(0, 0), new Position(0, 0)), "");
  }
};
var WorkspaceEdit = class {
  _edits = /* @__PURE__ */ new Map();
  /**
   * Set edits for a specific URI
   *
   * @param uri - The document URI
   * @param edits - Array of text edits
   */
  set(uri, edits) {
    this._edits.set(uri.toString(), edits);
  }
  /**
   * Get edits for a specific URI
   *
   * @param uri - The document URI
   * @returns Array of text edits, or empty array if none
   */
  get(uri) {
    return this._edits.get(uri.toString()) || [];
  }
  /**
   * Check if edits exist for a URI
   *
   * @param uri - The document URI
   * @returns true if edits exist
   */
  has(uri) {
    return this._edits.has(uri.toString());
  }
  /**
   * Add a delete edit for a range
   *
   * @param uri - The document URI
   * @param range - The range to delete
   */
  delete(uri, range) {
    const key = uri.toString();
    if (!this._edits.has(key)) {
      this._edits.set(key, []);
    }
    this._edits.get(key).push(TextEdit.delete(range));
  }
  /**
   * Add an insert edit
   *
   * @param uri - The document URI
   * @param position - The position to insert at
   * @param newText - The text to insert
   */
  insert(uri, position, newText) {
    const key = uri.toString();
    if (!this._edits.has(key)) {
      this._edits.set(key, []);
    }
    this._edits.get(key).push(TextEdit.insert(position, newText));
  }
  /**
   * Add a replace edit
   *
   * @param uri - The document URI
   * @param range - The range to replace
   * @param newText - The new text
   */
  replace(uri, range, newText) {
    const key = uri.toString();
    if (!this._edits.has(key)) {
      this._edits.set(key, []);
    }
    this._edits.get(key).push(TextEdit.replace(range, newText));
  }
  /**
   * Get the number of documents with edits
   */
  get size() {
    return this._edits.size;
  }
  /**
   * Get all URI and edits pairs
   *
   * @returns Array of [URI, TextEdit[]] pairs
   */
  entries() {
    return Array.from(this._edits.entries()).map(([uriString, edits]) => {
      return [{ toString: () => uriString, fsPath: uriString.replace(/^file:\/\//, "") }, edits];
    });
  }
};

// packages/vscode-shim/src/classes/Additional.ts
var Location = class {
  constructor(uri, range) {
    this.uri = uri;
    this.range = range;
  }
  uri;
  range;
};
var DiagnosticRelatedInformation = class {
  constructor(location, message) {
    this.location = location;
    this.message = message;
  }
  location;
  message;
};
var Diagnostic = class {
  range;
  message;
  severity;
  source;
  code;
  relatedInformation;
  tags;
  constructor(range, message, severity) {
    this.range = range;
    this.message = message;
    this.severity = severity !== void 0 ? severity : 0;
  }
};
var ThemeColor = class {
  constructor(id) {
    this.id = id;
  }
  id;
};
var ThemeIcon = class {
  constructor(id, color) {
    this.id = id;
    this.color = color;
  }
  id;
  color;
};
var CodeActionKind = class _CodeActionKind {
  constructor(value) {
    this.value = value;
  }
  value;
  static Empty = new _CodeActionKind("");
  static QuickFix = new _CodeActionKind("quickfix");
  static Refactor = new _CodeActionKind("refactor");
  static RefactorExtract = new _CodeActionKind("refactor.extract");
  static RefactorInline = new _CodeActionKind("refactor.inline");
  static RefactorRewrite = new _CodeActionKind("refactor.rewrite");
  static Source = new _CodeActionKind("source");
  static SourceOrganizeImports = new _CodeActionKind("source.organizeImports");
  append(parts) {
    return new _CodeActionKind(this.value ? `${this.value}.${parts}` : parts);
  }
  intersects(other) {
    return this.contains(other) || other.contains(this);
  }
  contains(other) {
    return this.value === other.value || other.value.startsWith(this.value + ".");
  }
};
var CodeLens = class {
  range;
  command;
  isResolved = false;
  constructor(range, command) {
    this.range = range;
    this.command = command;
  }
};
var LanguageModelTextPart = class {
  constructor(value) {
    this.value = value;
  }
  value;
};
var LanguageModelToolCallPart = class {
  constructor(callId, name, input) {
    this.callId = callId;
    this.name = name;
    this.input = input;
  }
  callId;
  name;
  input;
};
var LanguageModelToolResultPart = class {
  constructor(callId, content) {
    this.callId = callId;
    this.content = content;
  }
  callId;
  content;
};
var FileSystemError = class _FileSystemError extends Error {
  code;
  constructor(message, code = "Unknown") {
    super(message);
    this.name = "FileSystemError";
    this.code = code;
  }
  static FileNotFound(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `File not found: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "FileNotFound");
  }
  static FileExists(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `File exists: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "FileExists");
  }
  static FileNotADirectory(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `File is not a directory: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "FileNotADirectory");
  }
  static FileIsADirectory(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `File is a directory: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "FileIsADirectory");
  }
  static NoPermissions(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `No permissions: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "NoPermissions");
  }
  static Unavailable(messageOrUri) {
    const message = typeof messageOrUri === "string" ? messageOrUri : `Unavailable: ${messageOrUri?.fsPath || "unknown"}`;
    return new _FileSystemError(message, "Unavailable");
  }
};

// packages/vscode-shim/src/classes/CancellationToken.ts
var CancellationTokenSource = class {
  _token;
  _isCancelled = false;
  _onCancellationRequestedEmitter = new EventEmitter();
  constructor() {
    this._token = {
      isCancellationRequested: false,
      onCancellationRequested: this._onCancellationRequestedEmitter.event
    };
  }
  get token() {
    return this._token;
  }
  cancel() {
    if (!this._isCancelled) {
      this._isCancelled = true;
      this._token.isCancellationRequested = true;
      this._onCancellationRequestedEmitter.fire(void 0);
    }
  }
  dispose() {
    this.cancel();
    this._onCancellationRequestedEmitter.dispose();
  }
};

// packages/vscode-shim/src/utils/logger.ts
var ConsoleLogger = class {
  info(message, context, _meta) {
    console.log(`[${context || "INFO"}] ${message}`);
  }
  warn(message, context, _meta) {
    console.warn(`[${context || "WARN"}] ${message}`);
  }
  error(message, context, _meta) {
    console.error(`[${context || "ERROR"}] ${message}`);
  }
  debug(message, context, _meta) {
    if (process.env.DEBUG) {
      console.debug(`[${context || "DEBUG"}] ${message}`);
    }
  }
};
var logger = new ConsoleLogger();
var logs = {
  info: (message, context, meta) => logger.info(message, context, meta),
  warn: (message, context, meta) => logger.warn(message, context, meta),
  error: (message, context, meta) => logger.error(message, context, meta),
  debug: (message, context, meta) => logger.debug(message, context, meta)
};

// packages/vscode-shim/src/classes/OutputChannel.ts
var OutputChannel = class {
  _name;
  constructor(name) {
    this._name = name;
  }
  get name() {
    return this._name;
  }
  append(value) {
    logs.info(`[${this._name}] ${value}`, "VSCode.OutputChannel");
  }
  appendLine(value) {
    logs.info(`[${this._name}] ${value}`, "VSCode.OutputChannel");
  }
  clear() {
  }
  show() {
  }
  hide() {
  }
  dispose() {
  }
};

// packages/vscode-shim/src/classes/StatusBarItem.ts
var StatusBarItem = class {
  constructor(alignment, priority) {
    this.alignment = alignment;
    this.priority = priority;
  }
  alignment;
  priority;
  _text = "";
  _tooltip;
  _command;
  _color;
  _backgroundColor;
  _isVisible = false;
  get text() {
    return this._text;
  }
  set text(value) {
    this._text = value;
  }
  get tooltip() {
    return this._tooltip;
  }
  set tooltip(value) {
    this._tooltip = value;
  }
  get command() {
    return this._command;
  }
  set command(value) {
    this._command = value;
  }
  get color() {
    return this._color;
  }
  set color(value) {
    this._color = value;
  }
  get backgroundColor() {
    return this._backgroundColor;
  }
  set backgroundColor(value) {
    this._backgroundColor = value;
  }
  get isVisible() {
    return this._isVisible;
  }
  show() {
    this._isVisible = true;
  }
  hide() {
    this._isVisible = false;
  }
  dispose() {
    this._isVisible = false;
  }
};

// packages/vscode-shim/src/classes/TextEditorDecorationType.ts
var TextEditorDecorationType = class {
  key;
  constructor(key) {
    this.key = key;
  }
  dispose() {
  }
};

// packages/vscode-shim/src/context/ExtensionContext.ts
var path5 = __toESM(require("path"), 1);
var fs4 = __toESM(require("fs"), 1);

// packages/vscode-shim/src/storage/Memento.ts
var fs2 = __toESM(require("fs"), 1);
var path3 = __toESM(require("path"), 1);

// packages/vscode-shim/src/utils/paths.ts
var fs = __toESM(require("fs"), 1);
var path2 = __toESM(require("path"), 1);
var STORAGE_BASE_DIR = ".vscode-mock";
function getBaseStorageDir() {
  const homeDir = process.env.HOME || process.env.USERPROFILE || ".";
  return path2.join(homeDir, STORAGE_BASE_DIR);
}
function hashWorkspacePath(workspacePath2) {
  let hash = 0;
  for (let i = 0; i < workspacePath2.length; i++) {
    const char = workspacePath2.charCodeAt(i);
    hash = (hash << 5) - hash + char;
    hash = hash & hash;
  }
  return Math.abs(hash).toString(16);
}
function ensureDirectoryExists(dirPath) {
  try {
    if (!fs.existsSync(dirPath)) {
      fs.mkdirSync(dirPath, { recursive: true });
    }
  } catch (error) {
    console.warn(`Failed to create directory ${dirPath}:`, error);
  }
}
function initializeWorkspace(workspacePath2) {
  const dirs = [getGlobalStorageDir(), getWorkspaceStorageDir(workspacePath2), getLogsDir()];
  for (const dir of dirs) {
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true });
    }
  }
}
function getGlobalStorageDir() {
  return path2.join(getBaseStorageDir(), "global-storage");
}
function getWorkspaceStorageDir(workspacePath2) {
  const hash = hashWorkspacePath(workspacePath2);
  return path2.join(getBaseStorageDir(), "workspace-storage", hash);
}
function getLogsDir() {
  return path2.join(getBaseStorageDir(), "logs");
}
var VSCodeMockPaths = {
  initializeWorkspace,
  getGlobalStorageDir,
  getWorkspaceStorageDir,
  getLogsDir
};

// packages/vscode-shim/src/storage/Memento.ts
var FileMemento = class {
  data = {};
  filePath;
  /**
   * Create a new FileMemento
   *
   * @param filePath - Path to the JSON file for persistence
   */
  constructor(filePath) {
    this.filePath = filePath;
    this.loadFromFile();
  }
  /**
   * Load data from the JSON file
   */
  loadFromFile() {
    try {
      if (fs2.existsSync(this.filePath)) {
        const content = fs2.readFileSync(this.filePath, "utf-8");
        this.data = JSON.parse(content);
      }
    } catch (error) {
      console.warn(`Failed to load state from ${this.filePath}:`, error);
      this.data = {};
    }
  }
  /**
   * Save data to the JSON file
   */
  saveToFile() {
    try {
      const dir = path3.dirname(this.filePath);
      ensureDirectoryExists(dir);
      fs2.writeFileSync(this.filePath, JSON.stringify(this.data, null, 2));
    } catch (error) {
      console.warn(`Failed to save state to ${this.filePath}:`, error);
    }
  }
  get(key, defaultValue) {
    const value = this.data[key];
    return value !== void 0 && value !== null ? value : defaultValue;
  }
  /**
   * Update a value in storage
   *
   * @param key - The key to update
   * @param value - The value to store (undefined to delete)
   * @returns A promise that resolves when the update is complete
   */
  async update(key, value) {
    if (value === void 0) {
      delete this.data[key];
    } else {
      this.data[key] = value;
    }
    this.saveToFile();
  }
  /**
   * Get all keys in storage
   *
   * @returns An array of all keys
   */
  keys() {
    return Object.keys(this.data);
  }
  /**
   * Clear all data from storage
   */
  clear() {
    this.data = {};
    this.saveToFile();
  }
};

// packages/vscode-shim/src/storage/SecretStorage.ts
var fs3 = __toESM(require("fs"), 1);
var path4 = __toESM(require("path"), 1);
var FileSecretStorage = class {
  secrets = {};
  _onDidChange = new EventEmitter();
  filePath;
  /**
   * Create a new FileSecretStorage
   *
   * @param storagePath - Directory path where secrets.json will be stored
   */
  constructor(storagePath) {
    this.filePath = path4.join(storagePath, "secrets.json");
    this.loadFromFile();
  }
  /**
   * Load secrets from the JSON file
   */
  loadFromFile() {
    try {
      if (fs3.existsSync(this.filePath)) {
        const content = fs3.readFileSync(this.filePath, "utf-8");
        this.secrets = JSON.parse(content);
      }
    } catch (error) {
      console.warn(`Failed to load secrets from ${this.filePath}:`, error);
      this.secrets = {};
    }
  }
  /**
   * Save secrets to the JSON file with restrictive permissions
   */
  saveToFile() {
    try {
      const dir = path4.dirname(this.filePath);
      ensureDirectoryExists(dir);
      fs3.writeFileSync(this.filePath, JSON.stringify(this.secrets, null, 2));
      if (process.platform !== "win32") {
        try {
          fs3.chmodSync(this.filePath, 384);
        } catch {
        }
      }
    } catch (error) {
      console.warn(`Failed to save secrets to ${this.filePath}:`, error);
    }
  }
  /**
   * Retrieve a secret by key
   *
   * @param key - The secret key
   * @returns The secret value or undefined if not found
   */
  async get(key) {
    return this.secrets[key];
  }
  /**
   * Store a secret
   *
   * @param key - The secret key
   * @param value - The secret value
   */
  async store(key, value) {
    this.secrets[key] = value;
    this.saveToFile();
    this._onDidChange.fire({ key });
  }
  /**
   * Delete a secret
   *
   * @param key - The secret key to delete
   */
  async delete(key) {
    delete this.secrets[key];
    this.saveToFile();
    this._onDidChange.fire({ key });
  }
  /**
   * Event fired when a secret changes
   */
  get onDidChange() {
    return this._onDidChange.event;
  }
  /**
   * Clear all secrets (useful for testing)
   */
  clearAll() {
    this.secrets = {};
    this.saveToFile();
  }
};

// packages/vscode-shim/src/context/ExtensionContext.ts
var ExtensionContextImpl = class {
  subscriptions = [];
  workspaceState;
  globalState;
  secrets;
  extensionUri;
  extensionPath;
  environmentVariableCollection = {};
  storageUri;
  storagePath;
  globalStorageUri;
  globalStoragePath;
  logUri;
  logPath;
  extensionMode;
  extension;
  constructor(options) {
    this.extensionPath = options.extensionPath;
    this.extensionUri = Uri.file(options.extensionPath);
    this.extensionMode = options.extensionMode || 1;
    const baseStorageDir = options.storageDir || path5.join(process.env.HOME || process.env.USERPROFILE || ".", ".vscode-mock");
    const workspaceHash = hashWorkspacePath(options.workspacePath);
    this.globalStoragePath = path5.join(baseStorageDir, "global-storage");
    this.globalStorageUri = Uri.file(this.globalStoragePath);
    const workspaceStoragePath = path5.join(baseStorageDir, "workspace-storage", workspaceHash);
    this.storagePath = workspaceStoragePath;
    this.storageUri = Uri.file(workspaceStoragePath);
    this.logPath = path5.join(baseStorageDir, "logs");
    this.logUri = Uri.file(this.logPath);
    ensureDirectoryExists(this.globalStoragePath);
    ensureDirectoryExists(workspaceStoragePath);
    ensureDirectoryExists(this.logPath);
    this.workspaceState = new FileMemento(path5.join(workspaceStoragePath, "workspace-state.json"));
    const globalMemento = new FileMemento(path5.join(this.globalStoragePath, "global-state.json"));
    this.globalState = Object.assign(globalMemento, {
      setKeysForSync: (_keys) => {
      }
    });
    this.secrets = new FileSecretStorage(this.globalStoragePath);
    this.extension = this.loadExtensionMetadata();
  }
  /**
   * Load extension metadata from package.json
   */
  loadExtensionMetadata() {
    try {
      const packageJsonPath = path5.join(this.extensionPath, "package.json");
      if (fs4.existsSync(packageJsonPath)) {
        const packageJSON = JSON.parse(fs4.readFileSync(packageJsonPath, "utf-8"));
        const extensionId = `${packageJSON.publisher || "unknown"}.${packageJSON.name || "unknown"}`;
        return {
          id: extensionId,
          extensionUri: this.extensionUri,
          extensionPath: this.extensionPath,
          isActive: true,
          packageJSON,
          exports: void 0,
          extensionKind: 1,
          // UI
          activate: () => Promise.resolve(void 0)
        };
      }
    } catch {
    }
    return void 0;
  }
  /**
   * Dispose all subscriptions
   */
  dispose() {
    for (const subscription of this.subscriptions) {
      try {
        subscription.dispose();
      } catch (error) {
        console.error("Error disposing subscription:", error);
      }
    }
    this.subscriptions = [];
  }
};

// packages/vscode-shim/src/api/FileSystemAPI.ts
var fs5 = __toESM(require("fs"), 1);
var path6 = __toESM(require("path"), 1);
var FileSystemAPI = class {
  async stat(uri) {
    try {
      const stats = fs5.statSync(uri.fsPath);
      return {
        type: stats.isDirectory() ? 2 : 1,
        // Directory = 2, File = 1
        ctime: stats.ctimeMs,
        mtime: stats.mtimeMs,
        size: stats.size
      };
    } catch {
      return {
        type: 1,
        // File
        ctime: Date.now(),
        mtime: Date.now(),
        size: 0
      };
    }
  }
  async readFile(uri) {
    try {
      const content = fs5.readFileSync(uri.fsPath);
      return new Uint8Array(content);
    } catch (error) {
      if (error.code === "ENOENT") {
        throw FileSystemError.FileNotFound(uri);
      }
      throw new FileSystemError(`Failed to read file: ${uri.fsPath}`);
    }
  }
  async writeFile(uri, content) {
    try {
      const dir = path6.dirname(uri.fsPath);
      ensureDirectoryExists(dir);
      fs5.writeFileSync(uri.fsPath, content);
    } catch {
      throw new Error(`Failed to write file: ${uri.fsPath}`);
    }
  }
  async delete(uri) {
    try {
      fs5.unlinkSync(uri.fsPath);
    } catch {
      throw new Error(`Failed to delete file: ${uri.fsPath}`);
    }
  }
  async createDirectory(uri) {
    try {
      fs5.mkdirSync(uri.fsPath, { recursive: true });
    } catch {
      throw new Error(`Failed to create directory: ${uri.fsPath}`);
    }
  }
};

// packages/vscode-shim/src/api/WorkspaceConfiguration.ts
var path7 = __toESM(require("path"), 1);

// packages/vscode-shim/src/types.ts
var ConfigurationTarget = /* @__PURE__ */ ((ConfigurationTarget2) => {
  ConfigurationTarget2[ConfigurationTarget2["Global"] = 1] = "Global";
  ConfigurationTarget2[ConfigurationTarget2["Workspace"] = 2] = "Workspace";
  ConfigurationTarget2[ConfigurationTarget2["WorkspaceFolder"] = 3] = "WorkspaceFolder";
  return ConfigurationTarget2;
})(ConfigurationTarget || {});
var ExtensionMode = /* @__PURE__ */ ((ExtensionMode2) => {
  ExtensionMode2[ExtensionMode2["Production"] = 1] = "Production";
  ExtensionMode2[ExtensionMode2["Development"] = 2] = "Development";
  ExtensionMode2[ExtensionMode2["Test"] = 3] = "Test";
  return ExtensionMode2;
})(ExtensionMode || {});
var FileType = /* @__PURE__ */ ((FileType2) => {
  FileType2[FileType2["Unknown"] = 0] = "Unknown";
  FileType2[FileType2["File"] = 1] = "File";
  FileType2[FileType2["Directory"] = 2] = "Directory";
  FileType2[FileType2["SymbolicLink"] = 64] = "SymbolicLink";
  return FileType2;
})(FileType || {});
var ViewColumn = /* @__PURE__ */ ((ViewColumn2) => {
  ViewColumn2[ViewColumn2["Active"] = -1] = "Active";
  ViewColumn2[ViewColumn2["Beside"] = -2] = "Beside";
  ViewColumn2[ViewColumn2["One"] = 1] = "One";
  ViewColumn2[ViewColumn2["Two"] = 2] = "Two";
  ViewColumn2[ViewColumn2["Three"] = 3] = "Three";
  return ViewColumn2;
})(ViewColumn || {});
var UIKind = /* @__PURE__ */ ((UIKind2) => {
  UIKind2[UIKind2["Desktop"] = 1] = "Desktop";
  UIKind2[UIKind2["Web"] = 2] = "Web";
  return UIKind2;
})(UIKind || {});
var EndOfLine = /* @__PURE__ */ ((EndOfLine3) => {
  EndOfLine3[EndOfLine3["LF"] = 1] = "LF";
  EndOfLine3[EndOfLine3["CRLF"] = 2] = "CRLF";
  return EndOfLine3;
})(EndOfLine || {});
var StatusBarAlignment = /* @__PURE__ */ ((StatusBarAlignment2) => {
  StatusBarAlignment2[StatusBarAlignment2["Left"] = 1] = "Left";
  StatusBarAlignment2[StatusBarAlignment2["Right"] = 2] = "Right";
  return StatusBarAlignment2;
})(StatusBarAlignment || {});
var DiagnosticSeverity = /* @__PURE__ */ ((DiagnosticSeverity2) => {
  DiagnosticSeverity2[DiagnosticSeverity2["Error"] = 0] = "Error";
  DiagnosticSeverity2[DiagnosticSeverity2["Warning"] = 1] = "Warning";
  DiagnosticSeverity2[DiagnosticSeverity2["Information"] = 2] = "Information";
  DiagnosticSeverity2[DiagnosticSeverity2["Hint"] = 3] = "Hint";
  return DiagnosticSeverity2;
})(DiagnosticSeverity || {});
var DiagnosticTag = /* @__PURE__ */ ((DiagnosticTag2) => {
  DiagnosticTag2[DiagnosticTag2["Unnecessary"] = 1] = "Unnecessary";
  DiagnosticTag2[DiagnosticTag2["Deprecated"] = 2] = "Deprecated";
  return DiagnosticTag2;
})(DiagnosticTag || {});
var OverviewRulerLane = /* @__PURE__ */ ((OverviewRulerLane2) => {
  OverviewRulerLane2[OverviewRulerLane2["Left"] = 1] = "Left";
  OverviewRulerLane2[OverviewRulerLane2["Center"] = 2] = "Center";
  OverviewRulerLane2[OverviewRulerLane2["Right"] = 4] = "Right";
  OverviewRulerLane2[OverviewRulerLane2["Full"] = 7] = "Full";
  return OverviewRulerLane2;
})(OverviewRulerLane || {});
var DecorationRangeBehavior = /* @__PURE__ */ ((DecorationRangeBehavior2) => {
  DecorationRangeBehavior2[DecorationRangeBehavior2["OpenOpen"] = 0] = "OpenOpen";
  DecorationRangeBehavior2[DecorationRangeBehavior2["ClosedClosed"] = 1] = "ClosedClosed";
  DecorationRangeBehavior2[DecorationRangeBehavior2["OpenClosed"] = 2] = "OpenClosed";
  DecorationRangeBehavior2[DecorationRangeBehavior2["ClosedOpen"] = 3] = "ClosedOpen";
  return DecorationRangeBehavior2;
})(DecorationRangeBehavior || {});
var TextEditorRevealType = /* @__PURE__ */ ((TextEditorRevealType2) => {
  TextEditorRevealType2[TextEditorRevealType2["Default"] = 0] = "Default";
  TextEditorRevealType2[TextEditorRevealType2["InCenter"] = 1] = "InCenter";
  TextEditorRevealType2[TextEditorRevealType2["InCenterIfOutsideViewport"] = 2] = "InCenterIfOutsideViewport";
  TextEditorRevealType2[TextEditorRevealType2["AtTop"] = 3] = "AtTop";
  return TextEditorRevealType2;
})(TextEditorRevealType || {});

// packages/vscode-shim/src/api/WorkspaceConfiguration.ts
var runtimeConfig = /* @__PURE__ */ new Map();
function setRuntimeConfig(section, key, value) {
  const fullKey = `${section}.${key}`;
  runtimeConfig.set(fullKey, value);
  logs.debug(`Runtime config set: ${fullKey} = ${JSON.stringify(value)}`, "VSCode.MockWorkspaceConfiguration");
}
function setRuntimeConfigValues(section, values) {
  for (const [key, value] of Object.entries(values)) {
    if (value !== void 0) {
      setRuntimeConfig(section, key, value);
    }
  }
}
var MockWorkspaceConfiguration = class {
  section;
  globalMemento;
  workspaceMemento;
  constructor(section, context) {
    this.section = section;
    if (context) {
      this.globalMemento = context.globalState;
      this.workspaceMemento = context.workspaceState;
    } else {
      const globalStoragePath = VSCodeMockPaths.getGlobalStorageDir();
      const workspaceStoragePath = VSCodeMockPaths.getWorkspaceStorageDir(process.cwd());
      ensureDirectoryExists(globalStoragePath);
      ensureDirectoryExists(workspaceStoragePath);
      this.globalMemento = new FileMemento(path7.join(globalStoragePath, "configuration.json"));
      this.workspaceMemento = new FileMemento(path7.join(workspaceStoragePath, "configuration.json"));
    }
  }
  get(section, defaultValue) {
    const fullSection = this.section ? `${this.section}.${section}` : section;
    const runtimeValue = runtimeConfig.get(fullSection);
    if (runtimeValue !== void 0) {
      return runtimeValue;
    }
    const workspaceValue = this.workspaceMemento.get(fullSection);
    if (workspaceValue !== void 0 && workspaceValue !== null) {
      return workspaceValue;
    }
    const globalValue = this.globalMemento.get(fullSection);
    if (globalValue !== void 0 && globalValue !== null) {
      return globalValue;
    }
    return defaultValue;
  }
  has(section) {
    const fullSection = this.section ? `${this.section}.${section}` : section;
    return this.workspaceMemento.get(fullSection) !== void 0 || this.globalMemento.get(fullSection) !== void 0;
  }
  inspect(section) {
    const fullSection = this.section ? `${this.section}.${section}` : section;
    const workspaceValue = this.workspaceMemento.get(fullSection);
    const globalValue = this.globalMemento.get(fullSection);
    if (workspaceValue !== void 0 || globalValue !== void 0) {
      return {
        key: fullSection,
        defaultValue: void 0,
        globalValue,
        workspaceValue,
        workspaceFolderValue: void 0
      };
    }
    return void 0;
  }
  async update(section, value, configurationTarget) {
    const fullSection = this.section ? `${this.section}.${section}` : section;
    try {
      const memento = configurationTarget === 2 /* Workspace */ ? this.workspaceMemento : this.globalMemento;
      const scope = configurationTarget === 2 /* Workspace */ ? "workspace" : "global";
      await memento.update(fullSection, value);
      logs.debug(
        `Configuration updated: ${fullSection} = ${JSON.stringify(value)} (${scope})`,
        "VSCode.MockWorkspaceConfiguration"
      );
    } catch (error) {
      logs.error(`Failed to update configuration: ${fullSection}`, "VSCode.MockWorkspaceConfiguration", {
        error
      });
      throw error;
    }
  }
  // Additional method to reload configuration from disk
  reload() {
    logs.debug("Configuration reload requested", "VSCode.MockWorkspaceConfiguration");
  }
  // Method to get all configuration data (useful for debugging and generic config loading)
  getAllConfig() {
    const globalKeys = this.globalMemento.keys();
    const workspaceKeys = this.workspaceMemento.keys();
    const allConfig = {};
    for (const key of globalKeys) {
      const value = this.globalMemento.get(key);
      if (value !== void 0 && value !== null) {
        allConfig[key] = value;
      }
    }
    for (const key of workspaceKeys) {
      const value = this.workspaceMemento.get(key);
      if (value !== void 0 && value !== null) {
        allConfig[key] = value;
      }
    }
    return allConfig;
  }
};

// packages/vscode-shim/src/api/WorkspaceAPI.ts
var fs6 = __toESM(require("fs"), 1);
var path8 = __toESM(require("path"), 1);
var WorkspaceAPI = class {
  workspaceFolders;
  name;
  workspaceFile;
  fs;
  textDocuments = [];
  _onDidChangeWorkspaceFolders = new EventEmitter();
  _onDidOpenTextDocument = new EventEmitter();
  _onDidChangeTextDocument = new EventEmitter();
  _onDidCloseTextDocument = new EventEmitter();
  context;
  constructor(workspacePath2, context) {
    this.context = context;
    this.workspaceFolders = [
      {
        uri: Uri.file(workspacePath2),
        name: path8.basename(workspacePath2),
        index: 0
      }
    ];
    this.name = path8.basename(workspacePath2);
    this.fs = new FileSystemAPI();
  }
  asRelativePath(pathOrUri, includeWorkspaceFolder) {
    const fsPath = typeof pathOrUri === "string" ? pathOrUri : pathOrUri.fsPath;
    if (!this.workspaceFolders || this.workspaceFolders.length === 0) {
      return fsPath;
    }
    for (const folder of this.workspaceFolders) {
      const workspacePath2 = folder.uri.fsPath;
      const normalizedFsPath = path8.normalize(fsPath);
      const normalizedWorkspacePath = path8.normalize(workspacePath2);
      if (normalizedFsPath.startsWith(normalizedWorkspacePath)) {
        let relativePath = path8.relative(normalizedWorkspacePath, normalizedFsPath);
        if (includeWorkspaceFolder && this.workspaceFolders.length > 1) {
          relativePath = path8.join(folder.name, relativePath);
        }
        return relativePath;
      }
    }
    return fsPath;
  }
  onDidChangeWorkspaceFolders(listener) {
    return this._onDidChangeWorkspaceFolders.event(listener);
  }
  onDidChangeConfiguration(listener) {
    const emitter = new EventEmitter();
    return emitter.event(listener);
  }
  onDidChangeTextDocument(listener) {
    return this._onDidChangeTextDocument.event(listener);
  }
  onDidOpenTextDocument(listener) {
    logs.debug("Registering onDidOpenTextDocument listener", "VSCode.Workspace");
    return this._onDidOpenTextDocument.event(listener);
  }
  onDidCloseTextDocument(listener) {
    return this._onDidCloseTextDocument.event(listener);
  }
  getConfiguration(section) {
    return new MockWorkspaceConfiguration(section, this.context);
  }
  findFiles(_include, _exclude) {
    return Promise.resolve([]);
  }
  async openTextDocument(uri) {
    logs.debug(`openTextDocument called for: ${uri.fsPath}`, "VSCode.Workspace");
    let content = "";
    try {
      content = fs6.readFileSync(uri.fsPath, "utf-8");
      logs.debug(`File content read successfully, length: ${content.length}`, "VSCode.Workspace");
    } catch (error) {
      logs.warn(`Failed to read file: ${uri.fsPath}`, "VSCode.Workspace", { error });
    }
    const lines = content.split("\n");
    const document = {
      uri,
      fileName: uri.fsPath,
      languageId: "plaintext",
      version: 1,
      isDirty: false,
      isClosed: false,
      lineCount: lines.length,
      getText: (range) => {
        if (!range) {
          return content;
        }
        return lines.slice(range.start.line, range.end.line + 1).join("\n");
      },
      lineAt: (line) => {
        const text = lines[line] || "";
        return {
          text,
          range: new Range(new Position(line, 0), new Position(line, text.length)),
          rangeIncludingLineBreak: new Range(new Position(line, 0), new Position(line + 1, 0)),
          firstNonWhitespaceCharacterIndex: text.search(/\S/),
          isEmptyOrWhitespace: text.trim().length === 0
        };
      },
      offsetAt: (position) => {
        let offset = 0;
        for (let i = 0; i < position.line && i < lines.length; i++) {
          offset += (lines[i]?.length || 0) + 1;
        }
        offset += position.character;
        return offset;
      },
      positionAt: (offset) => {
        let currentOffset = 0;
        for (let i = 0; i < lines.length; i++) {
          const lineLength = (lines[i]?.length || 0) + 1;
          if (currentOffset + lineLength > offset) {
            return new Position(i, offset - currentOffset);
          }
          currentOffset += lineLength;
        }
        return new Position(lines.length - 1, lines[lines.length - 1]?.length || 0);
      },
      save: () => Promise.resolve(true),
      validateRange: (range) => range,
      validatePosition: (position) => position
    };
    this.textDocuments.push(document);
    logs.debug(`Document added to textDocuments array, total: ${this.textDocuments.length}`, "VSCode.Workspace");
    logs.debug("Waiting before firing onDidOpenTextDocument", "VSCode.Workspace");
    await new Promise((resolve) => setTimeout(resolve, 10));
    logs.debug("Firing onDidOpenTextDocument event", "VSCode.Workspace");
    this._onDidOpenTextDocument.fire(document);
    logs.debug("onDidOpenTextDocument event fired", "VSCode.Workspace");
    return document;
  }
  async applyEdit(edit) {
    try {
      for (const [uri, edits] of edit.entries()) {
        let filePath = uri.fsPath;
        if (process.platform === "win32" && filePath.startsWith("/")) {
          filePath = filePath.slice(1);
        }
        let content = "";
        try {
          content = fs6.readFileSync(filePath, "utf-8");
        } catch {
        }
        const sortedEdits = edits.sort((a, b) => {
          const lineDiff = b.range.start.line - a.range.start.line;
          if (lineDiff !== 0) return lineDiff;
          return b.range.start.character - a.range.start.character;
        });
        const lines = content.split("\n");
        for (const textEdit of sortedEdits) {
          const startLine = textEdit.range.start.line;
          const startChar = textEdit.range.start.character;
          const endLine = textEdit.range.end.line;
          const endChar = textEdit.range.end.character;
          if (startLine === endLine) {
            const line = lines[startLine] || "";
            lines[startLine] = line.substring(0, startChar) + textEdit.newText + line.substring(endChar);
          } else {
            const firstLine = lines[startLine] || "";
            const lastLine = lines[endLine] || "";
            const newContent2 = firstLine.substring(0, startChar) + textEdit.newText + lastLine.substring(endChar);
            lines.splice(startLine, endLine - startLine + 1, newContent2);
          }
        }
        const newContent = lines.join("\n");
        fs6.writeFileSync(filePath, newContent, "utf-8");
        const document = this.textDocuments.find((doc) => doc.uri.fsPath === filePath);
        if (document) {
          const newLines = newContent.split("\n");
          document.lineCount = newLines.length;
          document.getText = (range) => {
            if (!range) {
              return newContent;
            }
            return newLines.slice(range.start.line, range.end.line + 1).join("\n");
          };
          document.lineAt = (line) => {
            const text = newLines[line] || "";
            return {
              text,
              range: new Range(new Position(line, 0), new Position(line, text.length)),
              rangeIncludingLineBreak: new Range(new Position(line, 0), new Position(line + 1, 0)),
              firstNonWhitespaceCharacterIndex: text.search(/\S/),
              isEmptyOrWhitespace: text.trim().length === 0
            };
          };
          document.offsetAt = (position) => {
            let offset = 0;
            for (let i = 0; i < position.line && i < newLines.length; i++) {
              offset += (newLines[i]?.length || 0) + 1;
            }
            offset += position.character;
            return offset;
          };
          document.positionAt = (offset) => {
            let currentOffset = 0;
            for (let i = 0; i < newLines.length; i++) {
              const lineLength = (newLines[i]?.length || 0) + 1;
              if (currentOffset + lineLength > offset) {
                return new Position(i, offset - currentOffset);
              }
              currentOffset += lineLength;
            }
            return new Position(newLines.length - 1, newLines[newLines.length - 1]?.length || 0);
          };
        }
      }
      return true;
    } catch (error) {
      logs.error("Failed to apply workspace edit", "VSCode.Workspace", { error });
      return false;
    }
  }
  createFileSystemWatcher(_globPattern, _ignoreCreateEvents, _ignoreChangeEvents, _ignoreDeleteEvents) {
    const emitter = new EventEmitter();
    return {
      onDidChange: (listener) => emitter.event(listener),
      onDidCreate: (listener) => emitter.event(listener),
      onDidDelete: (listener) => emitter.event(listener),
      dispose: () => emitter.dispose()
    };
  }
  registerTextDocumentContentProvider(_scheme, _provider) {
    return { dispose: () => {
    } };
  }
};

// packages/vscode-shim/src/api/TabGroupsAPI.ts
var TabGroupsAPI = class {
  _onDidChangeTabs = new EventEmitter();
  _tabGroups = [];
  get all() {
    return this._tabGroups;
  }
  onDidChangeTabs(listener) {
    return this._onDidChangeTabs.event(listener);
  }
  async close(tab) {
    for (const group of this._tabGroups) {
      const index = group.tabs.indexOf(tab);
      if (index !== -1) {
        group.tabs.splice(index, 1);
        this._onDidChangeTabs.fire();
        return true;
      }
    }
    return false;
  }
  // Internal method to simulate tab changes for CLI
  _simulateTabChange() {
    this._onDidChangeTabs.fire();
  }
  dispose() {
    this._onDidChangeTabs.dispose();
  }
};

// packages/vscode-shim/src/api/WindowAPI.ts
var WindowAPI = class _WindowAPI {
  tabGroups;
  visibleTextEditors = [];
  _onDidChangeVisibleTextEditors = new EventEmitter();
  _workspace;
  static _decorationCounter = 0;
  constructor() {
    this.tabGroups = new TabGroupsAPI();
  }
  setWorkspace(workspace) {
    this._workspace = workspace;
  }
  createOutputChannel(name) {
    return new OutputChannel(name);
  }
  createStatusBarItem(idOrAlignment, alignmentOrPriority, priority) {
    let actualAlignment;
    let actualPriority;
    if (typeof idOrAlignment === "string") {
      actualAlignment = alignmentOrPriority ?? 1 /* Left */;
      actualPriority = priority;
    } else {
      actualAlignment = idOrAlignment ?? 1 /* Left */;
      actualPriority = alignmentOrPriority;
    }
    return new StatusBarItem(actualAlignment, actualPriority);
  }
  createTextEditorDecorationType(_options) {
    return new TextEditorDecorationType(`decoration-${++_WindowAPI._decorationCounter}`);
  }
  createTerminal(options) {
    return {
      name: options?.name || "Terminal",
      processId: Promise.resolve(void 0),
      creationOptions: options || {},
      exitStatus: void 0,
      state: { isInteractedWith: false },
      sendText: (text, _addNewLine) => {
        logs.debug(`Terminal sendText: ${text}`, "VSCode.Terminal");
      },
      show: (_preserveFocus) => {
        logs.debug("Terminal show called", "VSCode.Terminal");
      },
      hide: () => {
        logs.debug("Terminal hide called", "VSCode.Terminal");
      },
      dispose: () => {
        logs.debug("Terminal disposed", "VSCode.Terminal");
      }
    };
  }
  showInformationMessage(message, ..._items) {
    logs.info(message, "VSCode.Window");
    return Promise.resolve(void 0);
  }
  showWarningMessage(message, ..._items) {
    logs.warn(message, "VSCode.Window");
    return Promise.resolve(void 0);
  }
  showErrorMessage(message, ..._items) {
    logs.error(message, "VSCode.Window");
    return Promise.resolve(void 0);
  }
  showQuickPick(items, _options) {
    return Promise.resolve(items[0]);
  }
  showInputBox(_options) {
    return Promise.resolve("");
  }
  showOpenDialog(_options) {
    return Promise.resolve([]);
  }
  async showTextDocument(documentOrUri, columnOrOptions, _preserveFocus) {
    const uri = documentOrUri instanceof Uri ? documentOrUri : documentOrUri.uri;
    logs.debug(`showTextDocument called for: ${uri?.toString() || "unknown"}`, "VSCode.Window");
    const placeholderEditor = {
      document: { uri },
      selection: new Selection(new Position(0, 0), new Position(0, 0)),
      selections: [new Selection(new Position(0, 0), new Position(0, 0))],
      visibleRanges: [new Range(new Position(0, 0), new Position(0, 0))],
      options: {},
      viewColumn: typeof columnOrOptions === "number" ? columnOrOptions : 1 /* One */,
      edit: () => Promise.resolve(true),
      insertSnippet: () => Promise.resolve(true),
      setDecorations: () => {
      },
      revealRange: () => {
      },
      show: () => {
      },
      hide: () => {
      }
    };
    this.visibleTextEditors.push(placeholderEditor);
    logs.debug(
      `Placeholder editor added to visibleTextEditors, total: ${this.visibleTextEditors.length}`,
      "VSCode.Window"
    );
    let document = documentOrUri;
    if (documentOrUri instanceof Uri && this._workspace) {
      logs.debug("Opening document via workspace.openTextDocument", "VSCode.Window");
      document = await this._workspace.openTextDocument(uri);
      logs.debug("Document opened successfully", "VSCode.Window");
      placeholderEditor.document = document;
    }
    setImmediate(() => {
      logs.debug("Firing onDidChangeVisibleTextEditors event", "VSCode.Window");
      this._onDidChangeVisibleTextEditors.fire(this.visibleTextEditors);
      logs.debug("onDidChangeVisibleTextEditors event fired", "VSCode.Window");
    });
    logs.debug("Returning editor from showTextDocument", "VSCode.Window");
    return placeholderEditor;
  }
  registerWebviewViewProvider(viewId, provider, _options) {
    if (global.__extensionHost) {
      const extensionHost = global.__extensionHost;
      extensionHost.registerWebviewProvider(viewId, provider);
      const mockWebview = {
        postMessage: (message) => {
          if (global.__extensionHost) {
            ;
            global.__extensionHost.emit("extensionWebviewMessage", message);
          }
          return Promise.resolve(true);
        },
        onDidReceiveMessage: (listener) => {
          if (global.__extensionHost) {
            ;
            global.__extensionHost.on("webviewMessage", listener);
          }
          return { dispose: () => {
          } };
        },
        asWebviewUri: (uriArg) => {
          return Uri.parse(`vscode-webview://webview/${uriArg.path}`);
        },
        html: "",
        options: {},
        cspSource: "vscode-webview:"
      };
      if (provider.resolveWebviewView) {
        const mockWebviewView = {
          webview: mockWebview,
          viewType: viewId,
          title: viewId,
          description: void 0,
          badge: void 0,
          show: () => {
          },
          onDidChangeVisibility: () => ({ dispose: () => {
          } }),
          onDidDispose: () => ({ dispose: () => {
          } }),
          visible: true
        };
        (async () => {
          try {
            const context = {
              preserveFocus: false,
              isInitialSetup: extensionHost.isInInitialSetup()
            };
            logs.debug(
              `Calling resolveWebviewView with isInitialSetup=${context.isInitialSetup}`,
              "VSCode.Window"
            );
            await provider.resolveWebviewView(mockWebviewView, {}, {});
            extensionHost.markWebviewReady();
            logs.debug("Webview resolution complete, marked as ready", "VSCode.Window");
          } catch (error) {
            logs.error("Error resolving webview view", "VSCode.Window", { error });
          }
        })();
      }
    }
    return {
      dispose: () => {
        if (global.__extensionHost) {
          ;
          global.__extensionHost.unregisterWebviewProvider(viewId);
        }
      }
    };
  }
  registerUriHandler(_handler) {
    return {
      dispose: () => {
      }
    };
  }
  onDidChangeTextEditorSelection(listener) {
    const emitter = new EventEmitter();
    return emitter.event(listener);
  }
  onDidChangeActiveTextEditor(listener) {
    const emitter = new EventEmitter();
    return emitter.event(listener);
  }
  onDidChangeVisibleTextEditors(listener) {
    return this._onDidChangeVisibleTextEditors.event(listener);
  }
  // Terminal event handlers
  onDidCloseTerminal(_listener) {
    return { dispose: () => {
    } };
  }
  onDidOpenTerminal(_listener) {
    return { dispose: () => {
    } };
  }
  onDidChangeActiveTerminal(_listener) {
    return { dispose: () => {
    } };
  }
  onDidChangeTerminalDimensions(_listener) {
    return { dispose: () => {
    } };
  }
  onDidWriteTerminalData(_listener) {
    return { dispose: () => {
    } };
  }
  get activeTerminal() {
    return void 0;
  }
  get terminals() {
    return [];
  }
};

// packages/vscode-shim/src/api/CommandsAPI.ts
var CommandsAPI = class {
  commands = /* @__PURE__ */ new Map();
  registerCommand(command, callback) {
    this.commands.set(command, callback);
    return {
      dispose: () => {
        this.commands.delete(command);
      }
    };
  }
  executeCommand(command, ...rest) {
    const handler = this.commands.get(command);
    if (handler) {
      try {
        const result = handler(...rest);
        return Promise.resolve(result);
      } catch (error) {
        return Promise.reject(error);
      }
    }
    switch (command) {
      case "workbench.action.files.saveFiles":
      case "workbench.action.closeWindow":
      case "workbench.action.reloadWindow":
        return Promise.resolve(void 0);
      case "vscode.diff":
        return this.handleDiffCommand(
          rest[0],
          rest[1],
          rest[2],
          rest[3]
        );
      default:
        logs.warn(`Unknown command: ${command}`, "VSCode.Commands");
        return Promise.resolve(void 0);
    }
  }
  async handleDiffCommand(originalUri, modifiedUri, title, _options) {
    logs.info(`[DIFF] Handling vscode.diff command`, "VSCode.Commands", {
      originalUri: originalUri?.toString(),
      modifiedUri: modifiedUri?.toString(),
      title
    });
    if (!modifiedUri) {
      logs.warn("[DIFF] vscode.diff called without modified URI", "VSCode.Commands");
      return;
    }
    const workspace = global.vscode?.workspace;
    const window = global.vscode?.window;
    if (!workspace || !window) {
      logs.warn("[DIFF] VSCode APIs not available for diff command", "VSCode.Commands");
      return;
    }
    logs.info(
      `[DIFF] Current visibleTextEditors count: ${window.visibleTextEditors?.length || 0}`,
      "VSCode.Commands"
    );
    try {
      logs.info(`[DIFF] Looking for already-opened document: ${modifiedUri.fsPath}`, "VSCode.Commands");
      let document = workspace.textDocuments.find((doc) => doc.uri.fsPath === modifiedUri.fsPath);
      if (!document) {
        logs.info(`[DIFF] Document not found, opening: ${modifiedUri.fsPath}`, "VSCode.Commands");
        document = await workspace.openTextDocument(modifiedUri);
        logs.info(`[DIFF] Document opened successfully, lineCount: ${document.lineCount}`, "VSCode.Commands");
      } else {
        logs.info(`[DIFF] Found existing document, lineCount: ${document.lineCount}`, "VSCode.Commands");
      }
      const mockEditor = {
        document,
        selection: new Selection(new Position(0, 0), new Position(0, 0)),
        selections: [new Selection(new Position(0, 0), new Position(0, 0))],
        visibleRanges: [new Range(new Position(0, 0), new Position(0, 0))],
        options: {},
        viewColumn: 1 /* One */,
        edit: async (callback) => {
          const editBuilder = {
            replace: (_range, _text) => {
              logs.debug("Mock edit builder replace called", "VSCode.Commands");
            },
            insert: (_position, _text) => {
              logs.debug("Mock edit builder insert called", "VSCode.Commands");
            },
            delete: (_range) => {
              logs.debug("Mock edit builder delete called", "VSCode.Commands");
            },
            setEndOfLine: (_endOfLine) => {
              logs.debug("Mock edit builder setEndOfLine called", "VSCode.Commands");
            }
          };
          callback(editBuilder);
          return true;
        },
        insertSnippet: () => Promise.resolve(true),
        setDecorations: () => {
        },
        revealRange: () => {
        },
        show: () => {
        },
        hide: () => {
        }
      };
      if (!window.visibleTextEditors) {
        window.visibleTextEditors = [];
      }
      const existingEditor = window.visibleTextEditors.find(
        (e) => e.document.uri.fsPath === modifiedUri.fsPath
      );
      if (existingEditor) {
        logs.info(`[DIFF] Editor already in visibleTextEditors, updating it`, "VSCode.Commands");
        Object.assign(existingEditor, mockEditor);
      } else {
        logs.info(`[DIFF] Adding new mock editor to visibleTextEditors`, "VSCode.Commands");
        window.visibleTextEditors.push(mockEditor);
      }
      logs.info(`[DIFF] visibleTextEditors count: ${window.visibleTextEditors.length}`, "VSCode.Commands");
      logs.info(
        `[DIFF] Diff view simulation complete (events already fired by showTextDocument)`,
        "VSCode.Commands"
      );
    } catch (error) {
      logs.error("[DIFF] Error simulating diff view", "VSCode.Commands", { error });
    }
  }
};

// packages/vscode-shim/src/utils/machine-id.ts
var fs7 = __toESM(require("fs"), 1);
var path9 = __toESM(require("path"), 1);
var crypto = __toESM(require("crypto"), 1);
var os = __toESM(require("os"), 1);
function machineIdSync() {
  const homeDir = process.env.HOME || process.env.USERPROFILE || ".";
  const idPath = path9.join(homeDir, ".vscode-mock", ".machine-id");
  try {
    if (fs7.existsSync(idPath)) {
      return fs7.readFileSync(idPath, "utf-8").trim();
    }
  } catch {
  }
  const hostname2 = os.hostname();
  const randomData = crypto.randomBytes(16).toString("hex");
  const machineId = crypto.createHash("sha256").update(`${hostname2}-${randomData}`).digest("hex");
  try {
    const dir = path9.dirname(idPath);
    ensureDirectoryExists(dir);
    fs7.writeFileSync(idPath, machineId);
  } catch {
  }
  return machineId;
}

// packages/vscode-shim/src/api/create-vscode-api-mock.ts
var import_meta = {};
var Package = { version: "1.0.0" };
function createVSCodeAPIMock(extensionRootPath, workspacePath2, identity, options) {
  const context = new ExtensionContextImpl({
    extensionPath: extensionRootPath,
    workspacePath: workspacePath2,
    storageDir: options?.storageDir
  });
  const workspace = new WorkspaceAPI(workspacePath2, context);
  const window = new WindowAPI();
  const commands = new CommandsAPI();
  window.setWorkspace(workspace);
  const env = {
    appName: `wrapper|cli|cli|${Package.version}`,
    appRoot: options?.appRoot || import_meta.dirname,
    language: "en",
    machineId: identity?.machineId || machineIdSync(),
    sessionId: identity?.sessionId || "cli-session-id",
    remoteName: void 0,
    shell: process.env.SHELL || "/bin/bash",
    uriScheme: "vscode",
    uiKind: 1,
    // Desktop
    openExternal: async (uri) => {
      logs.info(`Would open external URL: ${uri.toString()}`, "VSCode.Env");
      return true;
    },
    clipboard: {
      readText: async () => {
        logs.debug("Clipboard read requested", "VSCode.Clipboard");
        return "";
      },
      writeText: async (text) => {
        logs.debug(
          `Clipboard write: ${text.substring(0, 100)}${text.length > 100 ? "..." : ""}`,
          "VSCode.Clipboard"
        );
      }
    }
  };
  return {
    version: "1.84.0",
    Uri,
    EventEmitter,
    ConfigurationTarget,
    ViewColumn,
    TextEditorRevealType,
    StatusBarAlignment,
    DiagnosticSeverity,
    DiagnosticTag,
    Position,
    Range,
    Selection,
    Location,
    Diagnostic,
    DiagnosticRelatedInformation,
    TextEdit,
    WorkspaceEdit,
    EndOfLine,
    UIKind,
    ExtensionMode,
    CodeActionKind,
    ThemeColor,
    ThemeIcon,
    DecorationRangeBehavior,
    OverviewRulerLane,
    StatusBarItem,
    CancellationToken: class CancellationTokenClass {
      isCancellationRequested = false;
      onCancellationRequested = (_listener) => ({ dispose: () => {
      } });
    },
    CancellationTokenSource,
    CodeLens,
    LanguageModelTextPart,
    LanguageModelToolCallPart,
    LanguageModelToolResultPart,
    ExtensionContext: ExtensionContextImpl,
    FileType,
    FileSystemError,
    Disposable: class DisposableClass {
      dispose() {
      }
      static from(...disposables) {
        return {
          dispose: () => {
            disposables.forEach((d) => d.dispose());
          }
        };
      }
    },
    TabInputText: class TabInputText {
      constructor(uri) {
        this.uri = uri;
      }
      uri;
    },
    TabInputTextDiff: class TabInputTextDiff {
      constructor(original, modified) {
        this.original = original;
        this.modified = modified;
      }
      original;
      modified;
    },
    workspace,
    window,
    commands,
    env,
    context,
    // Add more APIs as needed
    languages: {
      registerCodeActionsProvider: () => ({ dispose: () => {
      } }),
      registerCodeLensProvider: () => ({ dispose: () => {
      } }),
      registerCompletionItemProvider: () => ({ dispose: () => {
      } }),
      registerHoverProvider: () => ({ dispose: () => {
      } }),
      registerDefinitionProvider: () => ({ dispose: () => {
      } }),
      registerReferenceProvider: () => ({ dispose: () => {
      } }),
      registerDocumentSymbolProvider: () => ({ dispose: () => {
      } }),
      registerWorkspaceSymbolProvider: () => ({ dispose: () => {
      } }),
      registerRenameProvider: () => ({ dispose: () => {
      } }),
      registerDocumentFormattingEditProvider: () => ({ dispose: () => {
      } }),
      registerDocumentRangeFormattingEditProvider: () => ({ dispose: () => {
      } }),
      registerSignatureHelpProvider: () => ({ dispose: () => {
      } }),
      getDiagnostics: (uri) => {
        if (uri) {
          return [];
        }
        return [];
      },
      createDiagnosticCollection: (name) => {
        const diagnostics = /* @__PURE__ */ new Map();
        const collection = {
          name: name || "default",
          set: (uriOrEntries, diagnosticsOrUndefined) => {
            if (Array.isArray(uriOrEntries)) {
              for (const [uri, diags] of uriOrEntries) {
                if (diags === void 0) {
                  diagnostics.delete(uri.toString());
                } else {
                  diagnostics.set(uri.toString(), diags);
                }
              }
            } else {
              if (diagnosticsOrUndefined === void 0) {
                diagnostics.delete(uriOrEntries.toString());
              } else {
                diagnostics.set(uriOrEntries.toString(), diagnosticsOrUndefined);
              }
            }
          },
          delete: (uri) => {
            diagnostics.delete(uri.toString());
          },
          clear: () => {
            diagnostics.clear();
          },
          forEach: (callback, thisArg) => {
            diagnostics.forEach((diags, uriString) => {
              callback.call(thisArg, Uri.parse(uriString), diags, collection);
            });
          },
          get: (uri) => {
            return diagnostics.get(uri.toString());
          },
          has: (uri) => {
            return diagnostics.has(uri.toString());
          },
          dispose: () => {
            diagnostics.clear();
          }
        };
        return collection;
      }
    },
    debug: {
      onDidStartDebugSession: () => ({ dispose: () => {
      } }),
      onDidTerminateDebugSession: () => ({ dispose: () => {
      } })
    },
    tasks: {
      onDidStartTask: () => ({ dispose: () => {
      } }),
      onDidEndTask: () => ({ dispose: () => {
      } })
    },
    extensions: {
      all: [],
      getExtension: (extensionId) => {
        if (extensionId === "RooVeterinaryInc.roo-cline") {
          return {
            id: extensionId,
            extensionUri: context.extensionUri,
            extensionPath: context.extensionPath,
            isActive: true,
            packageJSON: {},
            exports: void 0,
            activate: () => Promise.resolve()
          };
        }
        return void 0;
      },
      onDidChange: () => ({ dispose: () => {
      } })
    },
    // Add file system watcher
    FileSystemWatcher: class {
      onDidChange = () => ({ dispose: () => {
      } });
      onDidCreate = () => ({ dispose: () => {
      } });
      onDidDelete = () => ({ dispose: () => {
      } });
      dispose = () => {
      };
    },
    // Add relative pattern
    RelativePattern: class {
      constructor(base, pattern) {
        this.base = base;
        this.pattern = pattern;
      }
      base;
      pattern;
    },
    // Add progress location
    ProgressLocation: {
      SourceControl: 1,
      Window: 10,
      Notification: 15
    },
    // Add URI handler
    UriHandler: class {
      handleUri = (_uri) => {
      };
    }
  };
}

// zoohost/src/host.ts
var extensionPath = process.env.ZOO_EXTENSION_PATH || import_path.default.resolve(__dirname, "..", "dist");
var workspacePath = process.env.ZOO_WORKSPACE || process.cwd();
var storageDir = process.env.ZOO_STORAGE_DIR || void 0;
var send = (obj) => {
  try {
    process.stdout.write(JSON.stringify(obj) + "\n");
  } catch {
  }
};
var log = (text) => send({ type: "log", text: String(text).slice(0, 500) });
var safeStr = (x) => {
  if (typeof x === "string") return x;
  try {
    return JSON.stringify(x) ?? String(x);
  } catch {
    return String(x);
  }
};
console.log = (...a) => log("[ext] " + a.map(safeStr).join(" "));
console.warn = (...a) => log("[ext:warn] " + a.map(safeStr).join(" "));
console.error = (...a) => log("[ext:err] " + a.map(safeStr).join(" "));
process.on("uncaughtException", (err) => log("[host:uncaught] " + (err?.stack || err)));
process.on("unhandledRejection", (err) => log("[host:rejection] " + (err?.stack || err)));
var ZooHost = class extends import_events.EventEmitter {
};
var host = new ZooHost();
var isReady = false;
host.registerWebviewProvider = () => {
};
host.isInInitialSetup = () => !isReady;
host.markWebviewReady = () => {
  if (isReady) return;
  isReady = true;
  try {
    setRuntimeConfigValues("zoo-code", {});
  } catch (e) {
    log("setRuntimeConfigValues \u5931\u8D25: " + e);
  }
  host.emit("webviewMessage", { type: "webviewDidLaunch" });
};
global.__extensionHost = host;
var vscode = createVSCodeAPIMock(extensionPath, workspacePath, void 0, { storageDir });
global.vscode = vscode;
{
  const envAny = vscode.env;
  if (typeof envAny.onDidChangeTelemetryEnabled !== "function") {
    envAny.onDidChangeTelemetryEnabled = (_cb) => ({ dispose: () => {
    } });
  }
  if (!envAny.appRoot) {
    envAny.appRoot = extensionPath;
  }
  const wsAny = vscode.workspace;
  if (typeof wsAny.getWorkspaceFolder !== "function") {
    wsAny.getWorkspaceFolder = (uri) => {
      const fsPath = typeof uri === "string" ? uri : uri?.fsPath ?? "";
      const folders = wsAny.workspaceFolders ?? [];
      for (const f of folders) {
        const root = f?.uri?.fsPath ?? "";
        if (root && String(fsPath).toLowerCase().startsWith(root.toLowerCase())) return f;
      }
      return void 0;
    };
  }
  if (typeof wsAny.openTextDocument !== "function") {
    const languageOf = (p) => {
      const ext = p.slice(p.lastIndexOf(".") + 1).toLowerCase();
      const map = {
        cs: "csharp",
        vb: "vb",
        ts: "typescript",
        tsx: "typescriptreact",
        js: "javascript",
        jsx: "javascriptreact",
        json: "json",
        md: "markdown",
        py: "python",
        fs: "fsharp",
        xml: "xml",
        xaml: "xml",
        css: "css",
        html: "html",
        yml: "yaml",
        yaml: "yaml"
      };
      return map[ext] ?? "plaintext";
    };
    const makeDoc = (fsPath) => {
      let text = "";
      try {
        text = import_fs.default.readFileSync(fsPath, "utf8");
      } catch {
      }
      const lines = text.split("\n");
      return {
        uri: { scheme: "file", path: fsPath.replace(/\\/g, "/"), fsPath, toString: () => "file:///" + fsPath.replace(/\\/g, "/") },
        fileName: fsPath,
        languageId: languageOf(fsPath),
        version: 1,
        isDirty: false,
        isUntitled: false,
        isClosed: false,
        eol: 1,
        lineCount: lines.length,
        getText: (range) => {
          if (!range) return text;
          try {
            return SliceByLines(
              text,
              range.start?.line ?? 0,
              range.start?.character ?? 0,
              range.end?.line ?? range.start?.line ?? 0,
              range.end?.character ?? range.start?.character ?? 0
            );
          } catch {
            return "";
          }
        },
        lineAt: (n) => {
          const t = lines[Math.max(0, Math.min(n, lines.length - 1))] ?? "";
          return { lineNumber: n, text: t.replace(/\r$/, ""), range: { start: { line: n, character: 0 }, end: { line: n, character: t.length } }, firstNonWhitespaceCharacterIndex: t.length - t.trimStart().length, isEmptyOrWhitespace: t.trim().length === 0 };
        },
        positionAt: (offset) => {
          const clamped = Math.max(0, Math.min(offset, text.length));
          const before = text.slice(0, clamped);
          const line = before.split("\n").length - 1;
          return { line, character: clamped - (before.lastIndexOf("\n") + 1) };
        },
        offsetAt: (pos) => {
          try {
            const starts = [0];
            for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) starts.push(i + 1);
            return (starts[Math.max(0, Math.min(pos?.line ?? 0, starts.length - 1))] ?? 0) + Math.max(0, pos?.character ?? 0);
          } catch {
            return 0;
          }
        },
        getWordRangeAtPosition: () => void 0,
        validateRange: (r) => r,
        validatePosition: (p) => p,
        save: async () => void 0
      };
    };
    wsAny.openTextDocument = async (arg) => makeDoc(typeof arg === "string" ? arg : arg?.fsPath ?? arg?.path ?? "");
  }
  if (typeof wsAny.onDidSaveTextDocument !== "function") {
    wsAny.onDidSaveTextDocument = (_cb) => ({ dispose: () => {
    } });
  }
  if (typeof wsAny.applyEdit !== "function") {
    wsAny.applyEdit = async (edit) => {
      try {
        const entries = typeof edit?.entries === "function" ? edit.entries() : [];
        for (const [uri, edits] of entries) {
          const file = uri?.fsPath ?? uri?.path;
          if (!file || !Array.isArray(edits) || edits.length === 0) continue;
          let text = "";
          try {
            text = import_fs.default.readFileSync(file, "utf8");
          } catch {
            continue;
          }
          const starts = [0];
          for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) starts.push(i + 1);
          const toOffset = (p) => (starts[Math.max(0, Math.min(p?.line ?? 0, starts.length - 1))] ?? 0) + Math.max(0, p?.character ?? 0);
          const ordered = edits.map((e) => ({ start: toOffset(e?.range?.start), end: toOffset(e?.range?.end), text: e?.newText ?? "" })).sort((a, b) => b.start - a.start);
          for (const e of ordered) {
            text = text.slice(0, e.start) + e.text + text.slice(Math.max(e.start, e.end));
          }
          import_fs.default.writeFileSync(file, text, "utf8");
        }
        return true;
      } catch (err) {
        log("[host] applyEdit \u5931\u8D25: " + err);
        return false;
      }
    };
  }
}
var req = (0, import_module.createRequire)(__filename);
var Module = req("module");
var originalResolve = Module._resolveFilename;
Module._resolveFilename = function(request, parent, isMain, options) {
  if (request === "vscode") return "vscode-mock";
  return originalResolve.call(this, request, parent, isMain, options);
};
req.cache["vscode-mock"] = {
  id: "vscode-mock",
  filename: "vscode-mock",
  loaded: true,
  exports: vscode,
  children: [],
  paths: [],
  path: "",
  isPreloading: false,
  parent: null,
  require: req
};
host.on("extensionWebviewMessage", (message) => {
  send({ type: "extensionMessage", message });
});
var envStore = { editor: null, diagnostics: [] };
var makeUri = (fsPath) => ({
  scheme: "file",
  path: fsPath.replace(/\\/g, "/"),
  fsPath,
  toString: () => "file:///" + fsPath.replace(/\\/g, "/")
});
var SliceByLines = (text, sl, sc, el, ec) => {
  const lineStarts = [0];
  for (let i = 0; i < text.length; i++) {
    if (text.charCodeAt(i) === 10) lineStarts.push(i + 1);
  }
  const clampLine = (l) => Math.max(0, Math.min(l, lineStarts.length - 1));
  const start = lineStarts[clampLine(sl)] + Math.max(0, sc);
  const end = lineStarts[clampLine(el)] + Math.max(0, ec);
  return text.slice(Math.min(start, text.length), Math.min(Math.max(start, end), text.length));
};
var toTextEditor = (e) => {
  if (!e) return void 0;
  const doc = {
    uri: makeUri(e.path),
    fileName: e.path,
    languageId: (e.language || "").toLowerCase(),
    version: 1,
    isDirty: !!e.isDirty,
    isUntitled: false,
    isClosed: false,
    eol: 1,
    lineCount: e.lineCount || 0,
    getText: (range) => {
      const text = e.text ?? "";
      if (!range) return text;
      try {
        return SliceByLines(text, range.start?.line ?? 0, range.start?.character ?? 0, range.end?.line ?? range.start?.line ?? 0, range.end?.character ?? range.start?.character ?? 0);
      } catch {
        return "";
      }
    },
    positionAt: () => ({ line: 0, character: 0 }),
    save: async () => {
    }
  };
  const sel = e.selection || {};
  const position = (l, c) => ({ line: l ?? 0, character: c ?? 0 });
  const active = position(sel.activeLine, sel.activeChar);
  const anchor = position(sel.anchorLine, sel.anchorChar);
  const selection = {
    active,
    anchor,
    start: active,
    end: anchor,
    isEmpty: active.line === anchor.line && active.character === anchor.character
  };
  return {
    document: doc,
    selection,
    selections: [selection],
    visibleRanges: [selection],
    viewColumn: 1,
    setDecorations: () => {
    },
    edit: async () => true,
    insertSnippet: async () => true,
    revealRange: () => {
    }
  };
};
var patchVscodeForInterop = () => {
  try {
    const call = globalThis.__zoovsCallHost;
    if (typeof call !== "function") return;
    const anyLang = vscode.languages;
    anyLang.getDiagnostics = (uri) => {
      if (uri && uri.fsPath) {
        const hit = envStore.diagnostics.find(([p]) => p === uri.fsPath || p.fsPath && p.fsPath === uri.fsPath);
        return hit ? hit[1] : [];
      }
      return envStore.diagnostics.map(([p, list]) => [makeUri(p), list]);
    };
    const anyWin = vscode.window;
    const tempDir = import_path.default.join(require("os").tmpdir(), "ZooVS", "diffs");
    const decodeLeftUri = (uriObj) => {
      try {
        const scheme = String(uriObj?.scheme ?? "");
        if (!scheme || scheme === "file") {
          const fp = uriObj?.fsPath ?? uriObj?.path ?? "";
          return fp ? String(fp) : null;
        }
        const raw = Buffer.from(String(uriObj?.query ?? ""), "base64").toString("utf8");
        const base = import_path.default.basename(String(uriObj?.path ?? uriObj?.toString() ?? "original.txt").split("?")[0]) || "original.txt";
        const fsMod = require("fs");
        fsMod.mkdirSync(tempDir, { recursive: true });
        const leftPath = import_path.default.join(tempDir, "left-" + Date.now() + "-" + base);
        fsMod.writeFileSync(leftPath, raw, "utf8");
        return leftPath;
      } catch (e) {
        log("[interop] left URI \u89E3\u7801\u5931\u8D25: " + e);
        return null;
      }
    };
    const origShow = anyWin.showTextDocument?.bind(anyWin);
    if (typeof origShow === "function") {
      anyWin.showTextDocument = async (...showArgs) => {
        const ed = await origShow(...showArgs);
        if (ed && typeof ed.setDecorations !== "function") {
          ed.setDecorations = () => {
          };
          if (typeof ed.insertSnippet !== "function") ed.insertSnippet = async () => true;
        }
        return ed;
      };
    }
    const eventStub = () => (_cb) => ({ dispose: () => {
    } });
    for (const name of [
      "onDidChangeTextEditorVisibleRanges",
      "onDidStartTerminalShellExecution",
      "onDidEndTerminalShellExecution",
      "onDidChangeTerminalShellIntegration"
    ]) {
      if (typeof anyWin[name] !== "function") anyWin[name] = eventStub();
    }
    if (typeof anyWin.withProgress !== "function") {
      anyWin.withProgress = async (_options, task) => await task(
        { report: () => {
        } },
        {
          isCancellationRequested: false,
          onCancellationRequested: () => ({ dispose: () => {
          } })
        }
      );
    }
    if (typeof anyWin.createWebviewPanel !== "function") {
      anyWin.createWebviewPanel = (..._args) => ({
        webview: {
          postMessage: async () => {
          },
          onDidReceiveMessage: () => ({ dispose: () => {
          } }),
          asWebviewUri: (u) => u,
          html: ""
        },
        onDidDispose: () => ({ dispose: () => {
        } }),
        onDidChangeViewState: () => ({ dispose: () => {
        } }),
        reveal: () => {
        },
        dispose: () => {
        },
        title: "",
        visible: true
      });
    }
    try {
      Object.defineProperty(anyWin, "activeTextEditor", {
        get: () => toTextEditor(envStore.editor),
        configurable: true
      });
      Object.defineProperty(anyWin, "visibleTextEditors", {
        get: () => envStore.editor ? [toTextEditor(envStore.editor)] : [],
        configurable: true
      });
    } catch (e) {
      log("[interop] activeTextEditor \u8865\u4E01\u5931\u8D25: " + e);
    }
    try {
      const anyCmd = vscode.commands;
      const origExecute = anyCmd.executeCommand?.bind(anyCmd) ?? (async () => void 0);
      anyCmd.executeCommand = async (command, ...args) => {
        if (command === "vscode.diff" || command === "vscode.diffSideBySide") {
          const l = decodeLeftUri(args[0]);
          const r = args[1]?.fsPath ?? args[1]?.path ?? String(args[1] ?? "");
          if (!l || !r) {
            log("[interop] vscode.diff \u53C2\u6570\u7F3A\u5931 left=" + l + " right=" + r);
            return;
          }
          return call("internal_open_diff", { leftPath: l, rightPath: r, title: args[2] });
        }
        return origExecute(command, ...args);
      };
    } catch (e) {
      log("[interop] diff \u547D\u4EE4\u8865\u4E01\u5931\u8D25: " + e);
    }
    const showMessage = (level) => async (message, ...rest) => {
      const items = rest.filter((x) => typeof x === "string").slice(0, 3);
      return await call("internal_show_message", { level, message, items });
    };
    try {
      anyWin.showInformationMessage = showMessage("info");
      anyWin.showWarningMessage = showMessage("warning");
      anyWin.showErrorMessage = showMessage("error");
      anyWin.showOpenDialog = async (options) => {
        const json = await call("internal_open_dialog", { canSelectMany: !!options?.canSelectMany });
        try {
          const arr = JSON.parse(String(json ?? "[]"));
          return arr.map((u) => makeUri(u.fsPath));
        } catch {
          return void 0;
        }
      };
      anyWin.showSaveDialog = async (_options) => {
        const json = await call("internal_save_dialog", {});
        try {
          const u = JSON.parse(String(json ?? "null"));
          return u && u.fsPath ? makeUri(u.fsPath) : void 0;
        } catch {
          return void 0;
        }
      };
    } catch (e) {
      log("[interop] \u5BF9\u8BDD\u6846\u8865\u4E01\u5931\u8D25: " + e);
    }
    try {
      const anyEnv = vscode.env;
      if (!anyEnv.clipboard) anyEnv.clipboard = {};
      anyEnv.clipboard.readText = async () => String(await call("internal_clipboard_read", {}) ?? "");
      anyEnv.clipboard.writeText = async (text) => {
        await call("internal_clipboard_write", { text: String(text ?? "") });
      };
      const origOpenExternal = anyEnv.openExternal?.bind(anyEnv) ?? (async () => false);
      anyEnv.openExternal = async (uri) => {
        const u = typeof uri === "string" ? uri : uri?.toString?.() ?? String(uri ?? "");
        return String(await call("internal_open_external", { uri: u })) === "true" || origOpenExternal(uri);
      };
    } catch (e) {
      log("[interop] \u526A\u8D34\u677F\u8865\u4E01\u5931\u8D25: " + e);
    }
    log("[interop] shim \u80FD\u529B\u8865\u4E01\u5C31\u7EEA(diff/\u5BF9\u8BDD\u6846/\u526A\u8D34\u677F/diagnostics/activeTextEditor)");
  } catch (e) {
    log("[interop] \u8865\u4E01\u5F02\u5E38: " + e);
  }
};
(async () => {
  log("\u5BBF\u4E3B\u542F\u52A8: extensionPath=" + extensionPath + " workspace=" + workspacePath);
  const bundlePath = import_path.default.join(extensionPath, "extension.js");
  const extensionModule = req(bundlePath);
  await extensionModule.activate(vscode.context);
  log("\u6269\u5C55\u6FC0\u6D3B\u5B8C\u6210");
  send({ type: "ready" });
})().catch((err) => {
  log("[host:activate \u5931\u8D25] " + (err?.stack || err));
  process.exitCode = 1;
});
var hostCalls = /* @__PURE__ */ new Map();
var hostCallSeq = 1;
global.__zoovsCallHost = (tool, args) => {
  return new Promise((resolve, reject) => {
    const id = hostCallSeq++;
    const timer = setTimeout(() => {
      hostCalls.delete(id);
      reject(new Error(`host call '${tool}' timed out after 300s`));
    }, 3e5);
    hostCalls.set(id, { resolve, reject, timer });
    send({ type: "hostCall", id, tool, args: args ?? {} });
  });
};
patchVscodeForInterop();
var rl = import_readline.default.createInterface({ input: process.stdin, terminal: false });
rl.on("line", (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  try {
    const msg = JSON.parse(trimmed);
    if (msg.type === "webviewReady") {
      ;
      host.markWebviewReady();
    } else if (msg.type === "webviewMessage" && msg.message) {
      host.emit("webviewMessage", msg.message);
    } else if (msg.type === "hostCallResult" && msg.id !== void 0) {
      const pending = hostCalls.get(msg.id);
      if (pending) {
        hostCalls.delete(msg.id);
        clearTimeout(pending.timer);
        if (msg.ok) pending.resolve(msg.result);
        else pending.reject(new Error(String(msg.error ?? "host call failed")));
      }
    } else if (msg.type === "workspaceChanged" && msg.workspace) {
      try {
        const wsAny2 = vscode.workspace;
        wsAny2.workspaceFolders = [
          { uri: vscode.Uri.file(String(msg.workspace)), name: import_path.default.basename(String(msg.workspace)), index: 0 }
        ];
        try {
          await(globalThis).__zoovsRefreshWorkspace?.();
        } catch (e) {
          log("[host] refreshWorkspace \u5931\u8D25: " + e);
        }
        log("[host] \u5DE5\u4F5C\u533A\u5DF2\u70ED\u66F4\u65B0: " + msg.workspace);
      } catch (e) {
        log("[host] \u5DE5\u4F5C\u533A\u70ED\u66F4\u65B0\u5931\u8D25: " + e);
      }
    } else if (msg.type === "envPush") {
      if ("editor" in msg) envStore.editor = msg.editor;
      if ("diagnostics" in msg) envStore.diagnostics = Array.isArray(msg.diagnostics) ? msg.diagnostics : [];
    }
  } catch (e) {
    log("[host] stdin \u89E3\u6790\u5931\u8D25: " + e);
  }
});
