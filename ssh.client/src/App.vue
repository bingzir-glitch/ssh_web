<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'

const profileStorageKey = 'web-ssh-connector-profile-v2'
const connectionsStorageKey = 'web-ssh-connector-saved-connections-v1'
const lastConnectionIdStorageKey = 'web-ssh-connector-last-connection-id-v1'
const fileCacheTtlMs = 8000
// 文件浏览器默认落在 /root，避免每次连接后还要手动切目录。
const defaultDirectory = '/root'

const builtInProfile = Object.freeze({
  name: '默认连接',
  host: '149.104.4.198',
  port: 22,
  username: 'root',
  password: '8sh96quFMKN5cZx8',
  directory: defaultDirectory,
})

const initialConnectionState = initializeConnectionState()
const legacySavedConnections = initialConnectionState.legacyConnections

const terminalHost = ref(null)
const terminalPanel = ref(null)
const terminalToolbar = ref(null)
const terminalShell = ref(null)
const fileTableWrap = ref(null)

const status = ref('idle')
const statusMessage = ref('远程链路待命。')
const terminalSize = ref('120 x 32')
const socketState = ref(WebSocket.CLOSED)
const pendingConnection = ref(false)

const currentDirectory = ref(initialConnectionState.profile.directory)
const parentDirectory = ref(null)
const fileEntries = ref([])
const filesLoading = ref(false)
const fileActionPending = ref(false)
const fileError = ref('')

const themeMode = ref(loadTheme())

const fileContextMenu = reactive({
  visible: false,
  x: 0,
  y: 0,
  entry: null,
})

const savedConnections = ref(initialConnectionState.connections)
const lastConnectionId = ref(initialConnectionState.lastConnectionId)
const form = reactive(initialConnectionState.profile)

// 目录列表做一个短时缓存，返回上一级或重新进入目录时会更顺手。
const directoryCache = new Map()

let terminal = null
let fitAddon = null
let terminalInputSubscription = null
let resizeObserver = null
let socket = null
let closingReason = ''
let disconnectRequested = false
let connectAbortController = null
let resizeFrame = 0
// 用递增请求号丢弃过期响应，避免快速切目录时旧结果覆盖新结果。
let latestFileRequestId = 0
let activeConnectionKey = ''

const canConnect = computed(() => {
  return (
    status.value !== 'connecting' &&
    form.host.trim().length > 0 &&
    form.username.trim().length > 0 &&
    form.password.trim().length > 0
  )
})

const canBrowseFiles = computed(() => {
  return (
    form.host.trim().length > 0 &&
    form.username.trim().length > 0 &&
    form.password.trim().length > 0
  )
})

const isConnected = computed(() => socketState.value === WebSocket.OPEN)

const directoryLabel = computed(() => currentDirectory.value || '未加载目录')

const themeToggleLabel = computed(() => (themeMode.value === 'dark' ? '日间模式' : '夜间模式'))

const savedConnectionsSummary = computed(() => {
  return savedConnections.value.length > 0
    ? `已保存 ${savedConnections.value.length} 条连接`
    : '暂无已保存连接'
})

const fileTypeExtensions = Object.freeze({
  image: new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.svg', '.bmp', '.ico', '.avif']),
  archive: new Set(['.zip', '.rar', '.7z', '.tar', '.gz', '.bz2', '.xz', '.tgz']),
  code: new Set(['.js', '.ts', '.jsx', '.tsx', '.vue', '.cs', '.java', '.cpp', '.c', '.h', '.hpp', '.py', '.go', '.rs', '.php', '.rb', '.swift', '.kt', '.html', '.css', '.scss', '.less', '.json', '.xml', '.yaml', '.yml', '.md']),
  config: new Set(['.env', '.conf', '.config', '.ini', '.toml', '.properties', '.service']),
  script: new Set(['.sh', '.bash', '.zsh', '.fish', '.ps1', '.bat', '.cmd']),
  document: new Set(['.txt', '.log', '.pdf', '.doc', '.docx', '.rtf', '.odt']),
  sheet: new Set(['.csv', '.xls', '.xlsx']),
  media: new Set(['.mp3', '.wav', '.flac', '.ogg', '.mp4', '.mov', '.mkv', '.avi', '.webm']),
  database: new Set(['.db', '.sqlite', '.sqlite3', '.sql']),
})

onMounted(() => {
  applyTheme(themeMode.value)
  initializeTerminal()
  window.addEventListener('pointerdown', handleWindowPointerDown)
  window.addEventListener('keydown', handleGlobalKeydown)

  void bootstrapPage()
})

onBeforeUnmount(() => {
  disconnect({ silent: true })
  terminalInputSubscription?.dispose()
  resizeObserver?.disconnect()
  if (resizeFrame) {
    cancelAnimationFrame(resizeFrame)
    resizeFrame = 0
  }
  window.removeEventListener('resize', scheduleTerminalLayout)
  window.removeEventListener('pointerdown', handleWindowPointerDown)
  window.removeEventListener('keydown', handleGlobalKeydown)
  terminal?.dispose()
})

function loadProfile() {
  let saved

  try {
    saved = JSON.parse(window.localStorage.getItem(profileStorageKey) ?? '{}')
  }
  catch {
    return null
  }

  if (!saved || typeof saved !== 'object') {
    return null
  }

  const host = typeof saved.host === 'string' ? saved.host.trim() : ''
  const username = typeof saved.username === 'string' ? saved.username.trim() : ''
  if (!host || !username) {
    return null
  }

  return createFormProfile(saved)
}

function initializeConnectionState() {
  const storedProfile = loadProfile()

  return {
    profile: createFormProfile(storedProfile ?? builtInProfile),
    connections: [],
    legacyConnections: loadLegacySavedConnections(),
    lastConnectionId: loadLastConnectionId(),
  }
}

function loadTheme() {
  return 'dark'
}

function saveProfile() {
  // 当前表单会完整保存，刷新页面后仍能延续最近一次连接状态。
  const profile = {
    name: buildConnectionName(form),
    host: form.host.trim(),
    port: normalizePort(form.port),
    username: form.username.trim(),
    password: form.password,
    directory: normalizePreferredDirectory(form.directory),
  }

  window.localStorage.setItem(profileStorageKey, JSON.stringify(profile))
}

function loadLegacySavedConnections() {
  try {
    const raw = JSON.parse(window.localStorage.getItem(connectionsStorageKey) ?? '[]')
    if (!Array.isArray(raw)) {
      return []
    }

    return raw
      .map(item => sanitizeSavedConnection(item))
      .filter(Boolean)
  }
  catch {
    return []
  }
}

function clearLegacySavedConnections() {
  window.localStorage.removeItem(connectionsStorageKey)
}

function loadLastConnectionId() {
  const value = window.localStorage.getItem(lastConnectionIdStorageKey)
  return typeof value === 'string' ? value : ''
}

function persistLastConnectionId(connectionId) {
  if (!connectionId) {
    window.localStorage.removeItem(lastConnectionIdStorageKey)
    return
  }

  window.localStorage.setItem(lastConnectionIdStorageKey, connectionId)
}

function rememberConnection(connectionId) {
  lastConnectionId.value = connectionId
  persistLastConnectionId(connectionId)
}

async function bootstrapPage() {
  await initializeSavedConnections()

  if (canConnect.value) {
    // 页面渲染并读取完后端保存连接后再自动连接，避免先用旧数据连错机器。
    await nextTick()
    void connect()
  }
}

async function initializeSavedConnections() {
  try {
    const serverConnections = await fetchSavedConnectionsFromServer()
    if (serverConnections.length > 0) {
      applySavedConnections(serverConnections)
      clearLegacySavedConnections()
      return
    }

    if (legacySavedConnections.length > 0) {
      const migratedConnections = await persistSavedConnectionsToServer(legacySavedConnections)
      applySavedConnections(migratedConnections)
      clearLegacySavedConnections()
      writeNotice('已将本地保存连接迁移到后端文件。', 'success')
      return
    }

    applySavedConnections([])
  }
  catch (error) {
    if (legacySavedConnections.length > 0) {
      applySavedConnections(legacySavedConnections)
      writeNotice('后端保存连接读取失败，已暂时回退到本地记录。', 'warning')
      return
    }

    writeNotice(
      error instanceof Error ? error.message : '已保存连接读取失败。',
      'warning',
    )
  }
}

async function fetchSavedConnectionsFromServer() {
  const response = await fetch('/api/ssh/connections')
  const payload = await tryReadJson(response)

  if (!response.ok) {
    throw new Error(getErrorMessage(payload, '已保存连接读取失败。'))
  }

  if (!Array.isArray(payload)) {
    return []
  }

  return payload
    .map(item => sanitizeSavedConnection(item))
    .filter(Boolean)
}

async function persistSavedConnectionsToServer(connections) {
  const normalizedConnections = connections
    .map(item => sanitizeSavedConnection(item))
    .filter(Boolean)

  const response = await fetch('/api/ssh/connections', {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify(normalizedConnections),
  })

  const payload = await tryReadJson(response)

  if (!response.ok) {
    throw new Error(getErrorMessage(payload, '保存连接失败。'))
  }

  if (!Array.isArray(payload)) {
    return normalizedConnections
  }

  return payload
    .map(item => sanitizeSavedConnection(item))
    .filter(Boolean)
}

function applySavedConnections(connections) {
  savedConnections.value = connections

  if (!connections.length) {
    rememberConnection('')
    saveProfile()
    return
  }

  const activeConnection =
    connections.find(connection => connection.id === lastConnectionId.value) ??
    connections.find(connection => getConnectionFingerprint(connection) === getConnectionFingerprint(form)) ??
    connections[0]

  rememberConnection(activeConnection.id)
  applyProfileToForm(activeConnection)
  saveProfile()
  resetFileBrowserState(activeConnection.directory)
}

function createFormProfile(source = {}) {
  const host = typeof source.host === 'string' && source.host.trim() ? source.host.trim() : builtInProfile.host
  const username = typeof source.username === 'string' && source.username.trim() ? source.username.trim() : builtInProfile.username

  return {
    name: buildConnectionName({
      name: source.name,
      host,
      username,
    }),
    host,
    port: normalizePort(source.port),
    username,
    password: typeof source.password === 'string' ? source.password : builtInProfile.password,
    directory: normalizePreferredDirectory(source.directory),
  }
}

function sanitizeSavedConnection(source = {}) {
  const host = typeof source.host === 'string' ? source.host.trim() : ''
  const username = typeof source.username === 'string' ? source.username.trim() : ''
  if (!host || !username) {
    return null
  }

  const password = typeof source.password === 'string' ? source.password : ''
  const connection = {
    id: typeof source.id === 'string' && source.id.trim() ? source.id.trim() : createConnectionId(),
    name: buildConnectionName({
      name: source.name,
      host,
      username,
    }),
    host,
    port: normalizePort(source.port),
    username,
    password,
    directory: normalizePreferredDirectory(source.directory),
    createdAt: typeof source.createdAt === 'string' && source.createdAt.trim()
      ? source.createdAt
      : new Date().toISOString(),
    updatedAt: typeof source.updatedAt === 'string' && source.updatedAt.trim()
      ? source.updatedAt
      : new Date().toISOString(),
  }

  return connection
}

function createSavedConnection(source = {}, overrides = {}) {
  const base = sanitizeSavedConnection({
    ...source,
    ...overrides,
  })

  return {
    ...base,
    id: overrides.id ?? base.id,
    createdAt: overrides.createdAt ?? base.createdAt,
    updatedAt: overrides.updatedAt ?? new Date().toISOString(),
  }
}

function createConnectionId() {
  if (globalThis.crypto?.randomUUID) {
    return globalThis.crypto.randomUUID()
  }

  return `connection-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}

function buildConnectionName(source = {}) {
  const customName = typeof source.name === 'string' ? source.name.trim() : ''
  const username = typeof source.username === 'string' ? source.username.trim() : ''
  const host = typeof source.host === 'string' ? source.host.trim() : ''
  const autoGeneratedName = username && host ? `${username}@${host}` : ''

  if (customName && customName !== autoGeneratedName) {
    return customName
  }

  return host || username || '未命名连接'
}

function normalizePort(value) {
  const port = Number(value)
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : builtInProfile.port
}

function normalizePreferredDirectory(value) {
  const nextValue = typeof value === 'string' ? value.trim() : ''
  return nextValue ? normalizeDirectoryPath(nextValue) : defaultDirectory
}

function applyProfileToForm(profile) {
  const nextProfile = createFormProfile(profile)
  form.name = nextProfile.name
  form.host = nextProfile.host
  form.port = nextProfile.port
  form.username = nextProfile.username
  form.password = nextProfile.password
  form.directory = nextProfile.directory
}

function loadSavedConnectionIntoForm(connection) {
  applyProfileToForm(connection)
  rememberConnection(connection.id)
  saveProfile()
  resetFileBrowserState(connection.directory)
}

function quickConnectSavedConnection(connection) {
  loadSavedConnectionIntoForm(connection)
  void nextTick(() => connect())
}

function getConnectionFingerprint(source) {
  return `${String(source.host ?? '').trim().toLowerCase()}::${normalizePort(source.port)}::${String(source.username ?? '').trim().toLowerCase()}`
}

async function syncCurrentConnection(options = {}) {
  const { announce = false } = options

  if (!form.host.trim() || !form.username.trim() || !form.password.trim()) {
    return null
  }

  const existingConnection = savedConnections.value.find(connection => getConnectionFingerprint(connection) === getConnectionFingerprint(form))
  const nextConnection = createSavedConnection(form, {
    id: existingConnection?.id,
    createdAt: existingConnection?.createdAt,
  })
  const nextConnections = existingConnection
    ? savedConnections.value.map(connection => (
        connection.id === nextConnection.id ? nextConnection : connection
      ))
    : [...savedConnections.value, nextConnection]
  const persistedConnections = await persistSavedConnectionsToServer(nextConnections)
  const persistedConnection =
    persistedConnections.find(connection => connection.id === nextConnection.id) ??
    persistedConnections.find(connection => getConnectionFingerprint(connection) === getConnectionFingerprint(nextConnection)) ??
    nextConnection

  savedConnections.value = persistedConnections
  rememberConnection(persistedConnection.id)
  applyProfileToForm(persistedConnection)
  saveProfile()

  if (announce) {
    writeNotice(`连接已保存：${persistedConnection.name}`, 'success')
  }

  return persistedConnection
}

async function removeSavedConnection(connectionId) {
  const targetConnection = savedConnections.value.find(connection => connection.id === connectionId)
  if (!targetConnection) {
    return
  }

  const confirmed = window.confirm(`确定要删除已保存连接“${targetConnection.name}”吗？`)
  if (!confirmed) {
    return
  }

  try {
    const nextConnections = savedConnections.value.filter(connection => connection.id !== connectionId)
    savedConnections.value = await persistSavedConnectionsToServer(nextConnections)

    if (lastConnectionId.value === connectionId) {
      rememberConnection(savedConnections.value[0]?.id ?? '')
    }

    writeNotice(`已删除保存连接：${targetConnection.name}`, 'warning')
  }
  catch (error) {
    writeNotice(
      error instanceof Error ? error.message : '删除保存连接失败。',
      'error',
    )
  }
}

async function clearSavedConnections() {
  if (!savedConnections.value.length) {
    return
  }

  const confirmed = window.confirm('确定清空所有已保存连接吗？')
  if (!confirmed) {
    return
  }

  try {
    await persistSavedConnectionsToServer([])
    savedConnections.value = []
    rememberConnection('')
    clearLegacySavedConnections()
    writeNotice('已清空全部保存连接。', 'warning')
  }
  catch (error) {
    writeNotice(
      error instanceof Error ? error.message : '清空保存连接失败。',
      'error',
    )
  }
}

function toggleTheme() {
  themeMode.value = themeMode.value === 'dark' ? 'light' : 'dark'
  applyTheme(themeMode.value)
}

function clearTerminalDisplay() {
  if (!terminal) {
    return
  }

  terminal.clear()

  if (isConnected.value) {
    // 同时给远端 shell 发送 Ctrl+L，让本地显示和远端终端状态保持一致。
    sendSocketMessage({
      type: 'input',
      data: '\u000c',
    })
  }

  focusTerminal()
}

function applyTheme(mode) {
  document.documentElement.dataset.theme = mode
  document.documentElement.style.colorScheme = mode

  if (terminal) {
    // 页面主题切换时同步刷新 xterm 主题，避免外层和终端颜色脱节。
    terminal.options.theme = buildTerminalTheme(mode)
    terminal.refresh(0, Math.max(0, terminal.rows - 1))
  }
}

function buildTerminalTheme(mode) {
  if (mode === 'light') {
    return {
      background: '#f6f8fb',
      foreground: '#17212b',
      cursor: '#0f766e',
      selectionBackground: 'rgba(15, 118, 110, 0.18)',
      black: '#1f2937',
      red: '#dc2626',
      green: '#0f766e',
      yellow: '#b45309',
      blue: '#2563eb',
      magenta: '#7c3aed',
      cyan: '#0891b2',
      white: '#f8fafc',
      brightBlack: '#475569',
      brightRed: '#ef4444',
      brightGreen: '#10b981',
      brightYellow: '#d97706',
      brightBlue: '#3b82f6',
      brightMagenta: '#8b5cf6',
      brightCyan: '#06b6d4',
      brightWhite: '#ffffff',
    }
  }

  return {
    background: '#040b16',
    foreground: '#d8ebff',
    cursor: '#4df5c5',
    selectionBackground: 'rgba(77, 245, 197, 0.2)',
    black: '#09111e',
    red: '#ff7b89',
    green: '#65f4b2',
    yellow: '#ffd36a',
    blue: '#68b6ff',
    magenta: '#ff9f66',
    cyan: '#55d8ff',
    white: '#edf6ff',
    brightBlack: '#3a577b',
    brightRed: '#ff98a2',
    brightGreen: '#86ffd0',
    brightYellow: '#ffe08e',
    brightBlue: '#96ceff',
    brightMagenta: '#ffbb8f',
    brightCyan: '#8ae7ff',
    brightWhite: '#ffffff',
  }
}

function initializeTerminal() {
  terminal = new Terminal({
    cursorBlink: true,
    convertEol: false,
    fontFamily: "'Cascadia Code', 'Consolas', 'Fira Code', monospace",
    fontSize: 14,
    lineHeight: 1.25,
    scrollback: 4000,
    theme: buildTerminalTheme(themeMode.value),
  })

  fitAddon = new FitAddon()
  terminal.loadAddon(fitAddon)
  terminal.open(terminalHost.value)
  scheduleTerminalLayout()

  // 终端里的每次键盘输入都原样透传给后端 shell。
  terminalInputSubscription = terminal.onData((data) => {
    sendSocketMessage({
      type: 'input',
      data,
    })
  })

  resizeObserver = new ResizeObserver(() => {
    scheduleTerminalLayout()
  })

  resizeObserver.observe(terminalPanel.value)
  resizeObserver.observe(terminalToolbar.value)
  resizeObserver.observe(terminalShell.value)
  window.addEventListener('resize', scheduleTerminalLayout)

  writeNotice('网页 SSH 连接器已就绪。')
  writeNotice('请填写主机信息并开始连接。')
  focusTerminal()
}

async function connect() {
  if (!canConnect.value) {
    return
  }

  form.name = buildConnectionName(form)
  form.directory = normalizePreferredDirectory(form.directory)

  const nextConnectionKey = getConnectionCacheKey()
  if (activeConnectionKey && activeConnectionKey !== nextConnectionKey) {
    // 切换到另一台主机时，当前目录和文件缓存要一起清掉。
    resetFileBrowserState(form.directory)
  }
  else if (!fileEntries.value.length && currentDirectory.value !== form.directory) {
    resetFileBrowserState(form.directory)
  }

  disconnect({ silent: true })
  saveProfile()
  connectAbortController?.abort()
  connectAbortController = new AbortController()

  status.value = 'connecting'
  statusMessage.value = `正在打开 ${form.username.trim()}@${form.host.trim()}:${Number(form.port) || 22}...`
  closingReason = ''
  disconnectRequested = false
  pendingConnection.value = true

  terminal.reset()
  scheduleTerminalLayout()
  writeNotice(statusMessage.value)
  focusTerminal()

  try {
    await syncCurrentConnection()
  }
  catch (error) {
    writeNotice(
      error instanceof Error
        ? `${error.message}，本次仍会继续尝试连接。`
        : '连接信息保存失败，本次仍会继续尝试连接。',
      'warning',
    )
  }

  try {
    const response = await fetch('/api/ssh/sessions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      signal: connectAbortController.signal,
      body: JSON.stringify({
        host: form.host.trim(),
        port: Number(form.port) || 22,
        username: form.username.trim(),
        password: normalizeSecret(form.password),
        privateKey: null,
        privateKeyPassphrase: null,
        columns: terminal?.cols ?? 120,
        rows: terminal?.rows ?? 32,
      }),
    })

    const payload = await tryReadJson(response)

    if (!response.ok) {
      throw new Error(getErrorMessage(payload, 'SSH 会话创建失败。'))
    }

    activeConnectionKey = nextConnectionKey
    connectAbortController = null
    openSocket(payload.sessionId)
  }
  catch (error) {
    pendingConnection.value = false

    if (error?.name === 'AbortError') {
      connectAbortController = null
      status.value = 'idle'
      statusMessage.value = '连接已断开。'
      return
    }

    connectAbortController = null
    status.value = 'error'
    statusMessage.value = error instanceof Error ? error.message : '发生了未知错误。'
    writeNotice(statusMessage.value, 'error')
  }
}

function disconnect(options = {}) {
  const { silent = false } = options

  disconnectRequested = true
  closingReason = silent ? closingReason : '连接已断开。'
  pendingConnection.value = false
  connectAbortController?.abort()
  connectAbortController = null

  if (socket && socket.readyState <= WebSocket.OPEN) {
    sendSocketMessage({ type: 'disconnect' })
    socket.close()
  }

  socket = null
  socketState.value = WebSocket.CLOSED

  if (!silent && status.value !== 'connecting') {
    status.value = 'idle'
    statusMessage.value = '连接已断开。'
    writeNotice(statusMessage.value)
  }
}

function openSocket(sessionId) {
  const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws'
  const url = `${scheme}://${window.location.host}/ws/ssh?sessionId=${encodeURIComponent(sessionId)}`

  const currentSocket = new WebSocket(url)
  socket = currentSocket
  socketState.value = currentSocket.readyState

  currentSocket.addEventListener('open', () => {
    if (socket !== currentSocket) {
      return
    }

    socketState.value = currentSocket.readyState
    pendingConnection.value = false
    status.value = 'connected'
    statusMessage.value = `已连接到 ${form.username.trim()}@${form.host.trim()}:${Number(form.port) || 22}`
    closingReason = ''
    disconnectRequested = false
    writeNotice(statusMessage.value, 'success')
    scheduleTerminalLayout()
    focusTerminal()
    // SSH 会话建立后再加载文件树，避免前后端状态不一致。
    void loadFiles(currentDirectory.value, { preferCache: true })
  })

  currentSocket.addEventListener('message', (event) => {
    if (socket !== currentSocket) {
      return
    }

    try {
      const message = JSON.parse(event.data)
      const messageType = message.type ?? message.Type
      const messageData = message.data ?? message.Data
      const messageText = message.message ?? message.Message

      if (messageType === 'data' && typeof messageData === 'string') {
        terminal.write(messageData)
      }
      else if (messageType === 'closed') {
        closingReason = messageText || 'SSH 会话已结束。'
      }
      else if (messageType === 'error') {
        status.value = 'error'
        statusMessage.value = messageText || 'SSH 传输发生错误。'
        closingReason = statusMessage.value
        writeNotice(statusMessage.value, 'error')
      }
    }
    catch {
      terminal.write(event.data)
    }
  })

  currentSocket.addEventListener('close', () => {
    if (socket !== currentSocket && (socket !== null || pendingConnection.value)) {
      return
    }

    if (socket === currentSocket) {
      socket = null
    }

    socketState.value = currentSocket.readyState
    pendingConnection.value = false

    const nextMessage =
      closingReason ||
      (disconnectRequested ? '连接已断开。' : 'SSH 会话已关闭。')

    if (status.value !== 'error') {
      status.value = 'idle'
      statusMessage.value = nextMessage
    }

    if (!disconnectRequested || (closingReason && closingReason !== '连接已断开。')) {
      writeNotice(nextMessage, disconnectRequested ? 'muted' : 'warning')
    }

    closingReason = ''
    disconnectRequested = false
  })

  currentSocket.addEventListener('error', () => {
    if (socket !== currentSocket) {
      return
    }

    socketState.value = currentSocket.readyState
    pendingConnection.value = false
    status.value = 'error'
    statusMessage.value = '终端建立完成前，WebSocket 连接失败。'
    closingReason = statusMessage.value
    writeNotice(statusMessage.value, 'error')
  })
}

function sendSocketMessage(message) {
  if (socket?.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(message))
  }
}

function fitTerminal() {
  if (!fitAddon || !terminal || !terminalPanel.value || !terminalToolbar.value || !terminalHost.value || !terminalShell.value) {
    return
  }

  const panelHeight = terminalPanel.value.getBoundingClientRect().height
  const toolbarHeight = terminalToolbar.value.getBoundingClientRect().height
  const shellHeight = terminalShell.value.getBoundingClientRect().height
  const shellStyles = window.getComputedStyle(terminalShell.value)
  const shellPaddingTop = Number.parseFloat(shellStyles.paddingTop) || 0
  const shellPaddingBottom = Number.parseFloat(shellStyles.paddingBottom) || 0
  const fallbackHeight = panelHeight - toolbarHeight - 36
  const availableHeight = shellHeight - shellPaddingTop - shellPaddingBottom
  const nextHeight = `${Math.max(260, Math.floor(availableHeight > 0 ? availableHeight : fallbackHeight))}px`

  if (terminalHost.value.style.height !== nextHeight) {
    terminalHost.value.style.height = nextHeight
  }

  const proposed = fitAddon.proposeDimensions()
  if (!proposed) {
    return
  }

  const maxCols = Math.max(80, Math.min(240, Math.floor(window.innerWidth / 7)))
  const maxRows = Math.max(20, Math.min(72, Math.floor(window.innerHeight / 18)))
  const nextCols = Math.max(40, Math.min(proposed.cols, maxCols))
  const nextRows = Math.max(12, Math.min(proposed.rows, maxRows))
  const previousSize = terminalSize.value

  if (terminal.cols !== nextCols || terminal.rows !== nextRows) {
    terminal.resize(nextCols, nextRows)
  }

  terminalSize.value = `${nextCols} x ${nextRows}`

  if (isConnected.value && terminalSize.value !== previousSize) {
    sendSocketMessage({
      type: 'resize',
      columns: nextCols,
      rows: nextRows,
    })
  }
}

function scheduleTerminalLayout() {
  if (resizeFrame) {
    cancelAnimationFrame(resizeFrame)
  }

  resizeFrame = requestAnimationFrame(() => {
    resizeFrame = 0
    fitTerminal()
  })
}

function focusTerminal() {
  terminal?.focus()
}

function writeNotice(message, tone = 'info') {
  if (!terminal) {
    return
  }

  const palette = {
    info: '\x1b[38;5;81m',
    success: '\x1b[38;5;114m',
    warning: '\x1b[38;5;221m',
    error: '\x1b[38;5;203m',
    muted: '\x1b[38;5;244m',
  }

  terminal.writeln(`${palette[tone] ?? palette.info}[网页SSH]\x1b[0m ${message}`)
}

async function loadFiles(path = currentDirectory.value, options = {}) {
  const {
    force = false,
    preferCache = true,
    preserveScroll = false,
  } = options

  if (!canBrowseFiles.value) {
    fileError.value = '请先填写主机、用户名和密码。'
    return
  }

  closeFileContextMenu()

  const requestPath = normalizeDirectoryPath(path || currentDirectory.value || '/')
  const scrollTop = preserveScroll ? getFileTableScrollTop() : 0
  const cached = preferCache ? getCachedDirectory(requestPath) : null

  if (cached) {
    // 先回填缓存，界面能立刻响应；后面再按需静默刷新远端数据。
    applyDirectoryListing(cached, { scrollTop, preserveScroll })

    if (!force && Date.now() - cached.cachedAt < fileCacheTtlMs) {
      filesLoading.value = false
      fileError.value = ''
      return
    }
  }

  filesLoading.value = true
  if (!cached) {
    fileError.value = ''
  }

  const requestId = ++latestFileRequestId

  try {
    const response = await fetch('/api/ssh/files/list', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        ...buildConnectionPayload(),
        path: requestPath,
      }),
    })

    const payload = await tryReadJson(response)

    if (!response.ok) {
      throw new Error(getErrorMessage(payload, '远程文件读取失败。'))
    }

    // 用户可能已经切到别的目录了，过期结果直接丢弃。
    if (requestId !== latestFileRequestId) {
      return
    }

    const listing = {
      path: payload.path ?? requestPath,
      parentPath: payload.parentPath ?? null,
      entries: Array.isArray(payload.entries) ? payload.entries : [],
      cachedAt: Date.now(),
    }

    setCachedDirectory(listing)
    applyDirectoryListing(listing, { scrollTop, preserveScroll })
    fileError.value = ''
  }
  catch (error) {
    if (requestId !== latestFileRequestId) {
      return
    }

    fileError.value = error instanceof Error ? error.message : '远程文件读取失败。'
  }
  finally {
    if (requestId === latestFileRequestId) {
      filesLoading.value = false
    }
  }
}

function buildConnectionPayload() {
  return {
    host: form.host.trim(),
    port: normalizePort(form.port),
    username: form.username.trim(),
    password: normalizeSecret(form.password),
  }
}

function openFileEntry(entry) {
  closeFileContextMenu()
  if (entry?.isDirectory) {
    void loadFiles(entry.path)
  }
}

function openParentDirectory() {
  if (parentDirectory.value) {
    void loadFiles(parentDirectory.value)
  }
}

function openFileContextMenu(event, entry) {
  event.preventDefault()
  event.stopPropagation()

  const menuWidth = 188
  const menuHeight = 188
  fileContextMenu.visible = true
  fileContextMenu.entry = entry
  fileContextMenu.x = Math.max(12, Math.min(event.clientX, window.innerWidth - menuWidth))
  fileContextMenu.y = Math.max(12, Math.min(event.clientY, window.innerHeight - menuHeight))
}

function closeFileContextMenu() {
  fileContextMenu.visible = false
  fileContextMenu.entry = null
}

function handleWindowPointerDown(event) {
  if (!fileContextMenu.visible) {
    return
  }

  const eventPath = typeof event.composedPath === 'function' ? event.composedPath() : []
  if (eventPath.some(node => node instanceof Element && node.classList.contains('context-menu'))) {
    return
  }

  closeFileContextMenu()
}

function handleGlobalKeydown(event) {
  if (event.key === 'Escape') {
    closeFileContextMenu()
  }
}

async function handleFileAction(action) {
  const targetEntry = fileContextMenu.entry
  closeFileContextMenu()

  if (fileActionPending.value) {
    return
  }

  if (!canBrowseFiles.value) {
    fileError.value = '请先填写主机、用户名和密码。'
    return
  }

  const currentPath = normalizeDirectoryPath(currentDirectory.value || '/')

  switch (action) {
    case 'rename': {
      if (!targetEntry) {
        return
      }

      const nextName = window.prompt('请输入新的名称', targetEntry.name)?.trim()
      if (!nextName || nextName === targetEntry.name) {
        return
      }

      await applyFileAction(
        {
          action,
          path: targetEntry.path,
          name: nextName,
        },
        () => renameLocalEntry(targetEntry, nextName),
      )
      break
    }
    case 'delete': {
      if (!targetEntry) {
        return
      }

      const confirmed = window.confirm(`确定要删除“${targetEntry.name}”吗？`)
      if (!confirmed) {
        return
      }

      await applyFileAction(
        {
          action,
          path: targetEntry.path,
        },
        () => removeLocalEntry(targetEntry.path),
      )
      break
    }
    case 'create-file': {
      const fileName = window.prompt('请输入新文件名')?.trim()
      if (!fileName) {
        return
      }

      await applyFileAction(
        {
          action,
          path: currentPath,
          name: fileName,
        },
        () => addLocalEntry(buildLocalEntry(currentPath, fileName, false)),
      )
      break
    }
    case 'create-directory': {
      const directoryName = window.prompt('请输入新文件夹名')?.trim()
      if (!directoryName) {
        return
      }

      await applyFileAction(
        {
          action,
          path: currentPath,
          name: directoryName,
        },
        () => addLocalEntry(buildLocalEntry(currentPath, directoryName, true)),
      )
      break
    }
  }
}

async function applyFileAction(payload, onSuccess) {
  fileActionPending.value = true
  fileError.value = ''

  try {
    const response = await fetch('/api/ssh/files/action', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        ...buildConnectionPayload(),
        ...payload,
      }),
    })

    const result = await tryReadJson(response)

    if (!response.ok) {
      throw new Error(getErrorMessage(result, '文件操作失败。'))
    }

    // 删除、重命名、新建优先做本地增量更新，避免整表刷新丢滚动位置。
    onSuccess?.()
  }
  catch (error) {
    fileError.value = error instanceof Error ? error.message : '文件操作失败。'
  }
  finally {
    fileActionPending.value = false
  }
}

function applyDirectoryListing(listing, options = {}) {
  const { preserveScroll = false, scrollTop = 0 } = options

  currentDirectory.value = listing.path ?? '/'
  parentDirectory.value = listing.parentPath ?? null
  fileEntries.value = sortEntries(cloneEntries(listing.entries))

  if (preserveScroll) {
    // 目录刷新后把滚动位置放回去，避免长列表每次都跳回顶部。
    void restoreFileTableScrollTop(scrollTop)
    return
  }

  void restoreFileTableScrollTop(0)
}

function setCachedDirectory(listing) {
  const path = normalizeDirectoryPath(listing.path)
  const normalizedEntries = sortEntries(cloneEntries(listing.entries))

  directoryCache.set(path, {
    path,
    parentPath: listing.parentPath ?? getParentPath(path),
    entries: normalizedEntries,
    cachedAt: listing.cachedAt ?? Date.now(),
  })
}

function getCachedDirectory(path) {
  const cached = directoryCache.get(normalizeDirectoryPath(path))
  if (!cached) {
    return null
  }

  return {
    path: cached.path,
    parentPath: cached.parentPath,
    entries: cloneEntries(cached.entries),
    cachedAt: cached.cachedAt,
  }
}

function resetFileBrowserState(directory = defaultDirectory) {
  directoryCache.clear()
  currentDirectory.value = normalizePreferredDirectory(directory)
  parentDirectory.value = null
  fileEntries.value = []
  fileError.value = ''
}

function renameLocalEntry(entry, nextName) {
  const parentPath = getParentPath(entry.path) ?? '/'
  const nextPath = combinePath(parentPath, nextName)

  mutateCurrentEntries(entries => entries.map((item) => {
    if (normalizePath(item.path) !== normalizePath(entry.path)) {
      return item
    }

    return {
      ...item,
      name: nextName,
      path: nextPath,
      modifiedAt: new Date().toISOString(),
    }
  }))
}

function removeLocalEntry(path) {
  mutateCurrentEntries(entries => entries.filter(entry => normalizePath(entry.path) !== normalizePath(path)))
}

function addLocalEntry(entry) {
  mutateCurrentEntries((entries) => {
    const nextEntries = entries.filter(item => normalizePath(item.path) !== normalizePath(entry.path))
    nextEntries.push(entry)
    return nextEntries
  })
}

function buildLocalEntry(parentPath, name, isDirectory) {
  const path = combinePath(parentPath, name)

  return {
    name,
    path,
    isDirectory,
    size: isDirectory ? null : 0,
    typeLabel: isDirectory ? '文件夹' : '文件',
    modifiedAt: new Date().toISOString(),
  }
}

function mutateCurrentEntries(transform) {
  // 当前目录列表和目录缓存始终一起更新，后续切回目录时才能秒开。
  const nextEntries = sortEntries(transform(cloneEntries(fileEntries.value)))
  fileEntries.value = nextEntries

  setCachedDirectory({
    path: currentDirectory.value || '/',
    parentPath: parentDirectory.value,
    entries: nextEntries,
    cachedAt: Date.now(),
  })
}

function cloneEntries(entries) {
  return Array.isArray(entries) ? entries.map(entry => ({ ...entry })) : []
}

function sortEntries(entries) {
  return [...entries].sort((left, right) => {
    if (left.isDirectory !== right.isDirectory) {
      return left.isDirectory ? -1 : 1
    }

    return String(left.name ?? '').localeCompare(String(right.name ?? ''), 'zh-CN', {
      sensitivity: 'base',
      numeric: true,
    })
  })
}

function getFileTableScrollTop() {
  return fileTableWrap.value?.scrollTop ?? 0
}

async function restoreFileTableScrollTop(scrollTop) {
  await nextTick()

  if (fileTableWrap.value) {
    fileTableWrap.value.scrollTop = scrollTop
  }
}

function getConnectionCacheKey() {
  return `${form.host.trim().toLowerCase()}::${normalizePort(form.port)}::${form.username.trim().toLowerCase()}`
}

function formatFileSize(size) {
  if (typeof size !== 'number' || Number.isNaN(size)) {
    return '--'
  }

  if (size < 1024) {
    return `${size} B`
  }

  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(1)} KB`
  }

  if (size < 1024 * 1024 * 1024) {
    return `${(size / (1024 * 1024)).toFixed(1)} MB`
  }

  return `${(size / (1024 * 1024 * 1024)).toFixed(1)} GB`
}

function formatDate(value) {
  if (!value) {
    return '--'
  }

  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return '--'
  }

  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function getFileIconKind(entry) {
  if (entry?.isDirectory) {
    return 'folder'
  }

  const name = String(entry?.name ?? '').toLowerCase()
  const dotIndex = name.lastIndexOf('.')
  const extension = dotIndex >= 0 ? name.slice(dotIndex) : ''

  if (!extension) {
    return 'document'
  }

  for (const [kind, extensions] of Object.entries(fileTypeExtensions)) {
    if (extensions.has(extension)) {
      return kind
    }
  }

  return 'document'
}

function normalizeSecret(value) {
  const normalized = value.trim()
  return normalized.length > 0 ? normalized : null
}

function normalizeDirectoryPath(path) {
  const normalized = normalizePath(path)
  return normalized || '/'
}

function normalizePath(path) {
  // 统一成和后端一致的绝对路径格式，减少重命名和删除时的路径比较问题。
  if (!path) {
    return '/'
  }

  let normalized = String(path).replaceAll('\\', '/').trim()
  if (!normalized) {
    return '/'
  }

  if (!normalized.startsWith('/')) {
    normalized = `/${normalized}`
  }

  while (normalized.length > 1 && normalized.endsWith('/')) {
    normalized = normalized.slice(0, -1)
  }

  return normalized
}

function getParentPath(path) {
  const normalized = normalizePath(path)
  if (normalized === '/') {
    return null
  }

  const lastSlashIndex = normalized.lastIndexOf('/')
  return lastSlashIndex <= 0 ? '/' : normalized.slice(0, lastSlashIndex)
}

function combinePath(directoryPath, name) {
  const normalizedDirectory = normalizeDirectoryPath(directoryPath)
  const normalizedName = String(name ?? '').trim()

  return normalizedDirectory === '/'
    ? `/${normalizedName}`
    : `${normalizedDirectory}/${normalizedName}`
}

async function tryReadJson(response) {
  const text = await response.text()

  if (!text) {
    return {}
  }

  try {
    return JSON.parse(text)
  }
  catch {
    return { message: text }
  }
}

function getErrorMessage(payload, fallback) {
  if (payload?.message) {
    return payload.message
  }

  if (payload?.errors) {
    return Object.values(payload.errors)
      .flat()
      .join(' ')
  }

  return fallback
}
</script>

<template>
  <div class="shell-page">
    <aside class="workspace-sidebar">
      <section class="saved-connections panel-card">
        <div class="section-heading">
          <div>
            <p class="eyebrow">快速连接</p>
            <strong>{{ savedConnectionsSummary }}</strong>
          </div>

          <button
            :disabled="!savedConnections.length"
            class="ghost-button"
            type="button"
            @click="clearSavedConnections"
          >
            清空
          </button>
        </div>

        <div v-if="savedConnections.length" class="saved-connections-list">
          <div
            v-for="connection in savedConnections"
            :key="connection.id"
            :class="{ 'saved-connection-row-active': connection.id === lastConnectionId }"
            class="saved-connection-row"
          >
            <div class="saved-connection-main">
              <span class="saved-connection-name">{{ connection.name }}</span>
            </div>

            <div class="saved-connection-actions">
              <button
                class="saved-connection-action"
                type="button"
                @click="quickConnectSavedConnection(connection)"
              >
                连接
              </button>
              <button
                class="saved-connection-action saved-connection-action-danger"
                type="button"
                @click="removeSavedConnection(connection.id)"
              >
                删除
              </button>
            </div>
          </div>
        </div>

        <div v-else class="saved-connections-empty">
          先保存一条常用连接，后面点一下就能直接连上。
        </div>
      </section>

      <form class="connect-form panel-card" @submit.prevent="connect">
        <div class="section-heading">
          <div>
            <p class="eyebrow">连接配置</p>
          </div>
        </div>

        <div class="form-grid">
          <label class="form-grid-span-2">
            <span>别名</span>
            <input v-model.trim="form.name" autocomplete="off" placeholder="例如 日本机房" />
          </label>

          <label class="form-grid-span-2">
            <span>主机地址</span>
            <input v-model.trim="form.host" autocomplete="off" placeholder="例如 192.168.1.10 或 server.example.com" />
          </label>

          <label>
            <span>端口</span>
            <input v-model.number="form.port" type="number" min="1" max="65535" />
          </label>

          <label>
            <span>用户名</span>
            <input v-model.trim="form.username" autocomplete="username" placeholder="例如 root" />
          </label>

          <label class="form-grid-span-2">
            <span>密码</span>
            <input v-model="form.password" type="password" autocomplete="current-password" placeholder="请输入 SSH 登录密码" />
          </label>
        </div>

        <div class="action-row">
          <button :disabled="!canConnect" class="primary" type="submit">
            {{ status === 'connecting' ? '连接中...' : '连接' }}
          </button>
          <button :disabled="!isConnected && status !== 'connecting'" class="secondary" type="button" @click="disconnect()">
            断开
          </button>
        </div>
      </form>
    </aside>

    <main class="workspace-main">
      <section ref="terminalPanel" class="terminal-panel panel-card">
        <div ref="terminalToolbar" class="terminal-toolbar">
          <div class="terminal-toolbar-copy">
            <p class="eyebrow">远程终端</p>
          </div>

          <div class="terminal-toolbar-actions">
            <button class="toolbar-action-button" type="button" @click="clearTerminalDisplay">
              清空终端
            </button>
            <button
              :aria-pressed="themeMode === 'light'"
              class="theme-toggle"
              type="button"
              @click="toggleTheme"
            >
              <svg aria-hidden="true" viewBox="0 0 20 20">
                <path
                  v-if="themeMode === 'dark'"
                  d="M10 3.2l1.2 2.5 2.8.4-2 1.9.5 2.8L10 9.6 7.5 10.8 8 8 6 6.1l2.8-.4zM15.6 12.8l.6 1.3 1.4.2-1 .9.2 1.4-1.2-.7-1.2.7.2-1.4-1-.9 1.4-.2zM5.1 12.5a3.4 3.4 0 1 0 4.8 4.8 6.3 6.3 0 0 1-4.8-4.8z"
                  fill="currentColor"
                />
                <path
                  v-else
                  d="M10 2.8l1 2.4 2.6.2-2 1.7.7 2.5-2.3-1.3-2.3 1.3.7-2.5-2-1.7 2.6-.2zM10 12.3a.9.9 0 1 1 0 1.8.9.9 0 0 1 0-1.8zm0-9.5a.9.9 0 0 1 .9.9V5a.9.9 0 0 1-1.8 0V3.7a.9.9 0 0 1 .9-.9zm0 11.7a.9.9 0 0 1 .9.9v1.3a.9.9 0 1 1-1.8 0v-1.3a.9.9 0 0 1 .9-.9zm6.3-4.5a.9.9 0 0 1 0 1.8H15a.9.9 0 0 1 0-1.8zm-11.3 0a.9.9 0 0 1 0 1.8H3.7a.9.9 0 0 1 0-1.8zm8.4-4a.9.9 0 0 1 1.3 0l.9.9a.9.9 0 1 1-1.3 1.3l-.9-.9a.9.9 0 0 1 0-1.3zm-8 8a.9.9 0 0 1 1.3 0l.9.9a.9.9 0 0 1-1.3 1.3l-.9-.9a.9.9 0 0 1 0-1.3zm9.3 1.3a.9.9 0 0 1-1.3 0l-.9-.9a.9.9 0 0 1 1.3-1.3l.9.9a.9.9 0 0 1 0 1.3zm-8-8a.9.9 0 0 1-1.3 0l-.9-.9A.9.9 0 0 1 6 5.1l.9.9a.9.9 0 0 1 0 1.3z"
                  fill="currentColor"
                />
              </svg>
              {{ themeToggleLabel }}
            </button>
          </div>
        </div>

        <div ref="terminalShell" class="terminal-shell">
          <div ref="terminalHost" class="terminal-surface" @mousedown="focusTerminal" />
        </div>
      </section>

      <section class="file-browser panel-card workspace-files">
        <div class="file-browser-header">
          <div class="file-browser-title">
            <p class="eyebrow">远程文件</p>
            <strong>{{ directoryLabel }}</strong>
          </div>

          <div class="file-browser-actions">
            <button
              :disabled="!parentDirectory || filesLoading || fileActionPending"
              class="ghost-button"
              type="button"
              @click="openParentDirectory"
            >
              上一级
            </button>
            <button
              :disabled="!canBrowseFiles || filesLoading || fileActionPending"
              class="ghost-button"
              type="button"
              @click="loadFiles(currentDirectory, { preserveScroll: true, force: true })"
            >
              刷新
            </button>
          </div>
        </div>

        <div
          ref="fileTableWrap"
          :class="{ 'file-table-wrap-loading': filesLoading && fileEntries.length > 0 }"
          class="file-table-wrap"
          @contextmenu.prevent="openFileContextMenu($event, null)"
        >
          <div v-if="filesLoading && !fileEntries.length" class="file-state">
            正在读取远程文件...
          </div>

          <div v-else-if="fileError && !fileEntries.length" class="file-state file-state-error">
            {{ fileError }}
          </div>

          <div v-else-if="!fileEntries.length" class="file-state">
            当前目录暂无文件。点击“刷新”即可重新读取。
          </div>

          <table v-else class="file-table">
            <thead>
              <tr>
                <th>名称</th>
                <th>类型</th>
                <th>大小</th>
                <th>修改时间</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="entry in fileEntries"
                :key="entry.path"
                :data-directory="entry.isDirectory"
                class="file-row"
                @contextmenu.prevent.stop="openFileContextMenu($event, entry)"
              >
                <td>
                  <button class="file-row-button" type="button" @dblclick="openFileEntry(entry)" @click="openFileEntry(entry)">
                    <span :class="`file-entry-icon-shell file-entry-icon-${getFileIconKind(entry)}`">
                      <svg :class="`file-entry-icon file-entry-icon-${getFileIconKind(entry)}`" aria-hidden="true" viewBox="0 0 20 20">
                        <g v-if="getFileIconKind(entry) === 'folder'">
                          <path d="M2 6.2h6l1.4-2H18a1 1 0 0 1 1 1v1H2z" fill="currentColor" opacity=".72" />
                          <path d="M2 7.2h17v7.9A1.9 1.9 0 0 1 17.1 17H3.9A1.9 1.9 0 0 1 2 15.1z" fill="currentColor" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'image'">
                          <path d="M5 2.5h7l3 3V17a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3.5a1 1 0 0 1 1-1z" fill="currentColor" opacity=".18" />
                          <path d="M12 2.5v3h3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                          <rect x="5.5" y="6.5" width="9" height="8" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2" />
                          <circle cx="8.4" cy="9.2" r="1.1" fill="currentColor" />
                          <path d="M6.3 13.2l2.4-2.4 1.9 1.6 1.8-2 2.1 2.8" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'archive'">
                          <rect x="4.2" y="4.5" width="11.6" height="12.5" rx="1.8" fill="currentColor" opacity=".16" />
                          <path d="M7 4.5h6" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4" />
                          <path d="M10 6.3v8.1" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.4" />
                          <path d="M8.4 8.6h3.2M8.4 10.8h3.2M8.4 13h3.2" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'code'">
                          <path d="M5 2.5h7l3 3V17a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3.5a1 1 0 0 1 1-1z" fill="currentColor" opacity=".16" />
                          <path d="M12 2.5v3h3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                          <path d="M8.1 9.1L6 11l2.1 1.9M11.9 9.1L14 11l-2.1 1.9M10.6 8.2l-1.2 5.6" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'config'">
                          <path d="M5 2.5h7l3 3V17a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3.5a1 1 0 0 1 1-1z" fill="currentColor" opacity=".16" />
                          <path d="M12 2.5v3h3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                          <path d="M7 9h6M7 12h6M9 7.8v2.4M12.5 10.8v2.4" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2" />
                          <circle cx="9" cy="9" r=".9" fill="currentColor" />
                          <circle cx="12.5" cy="12" r=".9" fill="currentColor" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'script'">
                          <rect x="3.8" y="4.2" width="12.4" height="11.6" rx="1.8" fill="currentColor" opacity=".16" />
                          <path d="M6.5 8.5l2.2 2.2-2.2 2.1M10.8 12.9h2.8" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.3" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'sheet'">
                          <path d="M5 2.5h7l3 3V17a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3.5a1 1 0 0 1 1-1z" fill="currentColor" opacity=".16" />
                          <path d="M12 2.5v3h3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                          <path d="M6.8 8.2h6.4M6.8 11h6.4M6.8 13.8h6.4M9.4 6.6v8.8M12 6.6v8.8" fill="none" stroke="currentColor" stroke-width="1.05" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'database'">
                          <ellipse cx="10" cy="5.5" rx="4.8" ry="1.9" fill="currentColor" opacity=".2" />
                          <path d="M5.2 5.5v6.7c0 1 2.1 1.9 4.8 1.9s4.8-.9 4.8-1.9V5.5" fill="none" stroke="currentColor" stroke-width="1.2" />
                          <path d="M5.2 8.8c0 1 2.1 1.9 4.8 1.9s4.8-.9 4.8-1.9M5.2 12c0 1 2.1 1.9 4.8 1.9s4.8-.9 4.8-1.9" fill="none" stroke="currentColor" stroke-width="1.2" />
                        </g>
                        <g v-else-if="getFileIconKind(entry) === 'media'">
                          <rect x="4.2" y="4.2" width="11.6" height="11.6" rx="2.2" fill="currentColor" opacity=".16" />
                          <path d="M8.2 7.1l5.1 3-5.1 3.1z" fill="currentColor" />
                        </g>
                        <g v-else>
                          <path d="M5 2.5h7l3 3V17a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V3.5a1 1 0 0 1 1-1z" fill="currentColor" opacity=".18" />
                          <path d="M12 2.5v3h3" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" stroke-width="1.2" />
                          <path d="M7.1 9h5.8M7.1 11.4h5.8M7.1 13.8h4.2" fill="none" stroke="currentColor" stroke-linecap="round" stroke-width="1.2" />
                        </g>
                      </svg>
                    </span>
                    <span class="file-entry-name">{{ entry.name }}</span>
                  </button>
                </td>
                <td>{{ entry.typeLabel }}</td>
                <td>{{ formatFileSize(entry.size) }}</td>
                <td>{{ formatDate(entry.modifiedAt) }}</td>
              </tr>
            </tbody>
          </table>

          <div v-if="filesLoading && fileEntries.length > 0" class="file-loading-overlay">
            正在同步目录...
          </div>

          <div v-if="fileError && fileEntries.length > 0" class="file-inline-error">
            {{ fileError }}
          </div>
        </div>
      </section>
    </main>

    <Teleport to="body">
      <div
        v-if="fileContextMenu.visible"
        :style="{ left: `${fileContextMenu.x}px`, top: `${fileContextMenu.y}px` }"
        class="context-menu"
        @click.stop
        @mousedown.stop
        @pointerdown.stop
        @contextmenu.prevent.stop
      >
        <button
          :disabled="!fileContextMenu.entry || fileActionPending"
          class="context-menu-item"
          type="button"
          @mousedown.stop
          @click.stop="handleFileAction('rename')"
        >
          重命名
        </button>
        <button
          :disabled="!fileContextMenu.entry || fileActionPending"
          class="context-menu-item context-menu-item-danger"
          type="button"
          @mousedown.stop
          @click.stop="handleFileAction('delete')"
        >
          删除
        </button>
        <button
          :disabled="fileActionPending"
          class="context-menu-item"
          type="button"
          @mousedown.stop
          @click.stop="handleFileAction('create-file')"
        >
          新建文件
        </button>
        <button
          :disabled="fileActionPending"
          class="context-menu-item"
          type="button"
          @mousedown.stop
          @click.stop="handleFileAction('create-directory')"
        >
          新建文件夹
        </button>
      </div>
    </Teleport>
  </div>
</template>
