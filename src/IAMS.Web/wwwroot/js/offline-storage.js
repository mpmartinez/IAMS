// IAMS Offline Storage - IndexedDB Module
const DB_NAME = 'iams-offline';
const DB_VERSION = 1;

const STORES = {
    assets: 'assets',
    pendingActions: 'pendingActions',
    metadata: 'metadata'
};

let db = null;

// Initialize the database
async function initDatabase() {
    if (db) return db;

    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, DB_VERSION);

        request.onerror = () => reject(request.error);
        request.onsuccess = () => {
            db = request.result;
            resolve(db);
        };

        request.onupgradeneeded = (event) => {
            const database = event.target.result;

            // Assets store - keyed by id
            if (!database.objectStoreNames.contains(STORES.assets)) {
                const assetsStore = database.createObjectStore(STORES.assets, { keyPath: 'id' });
                assetsStore.createIndex('assetTag', 'assetTag', { unique: true });
                assetsStore.createIndex('status', 'status', { unique: false });
                assetsStore.createIndex('assignedToUserId', 'assignedToUserId', { unique: false });
            }

            // Pending actions store - keyed by auto-increment id
            if (!database.objectStoreNames.contains(STORES.pendingActions)) {
                const actionsStore = database.createObjectStore(STORES.pendingActions, {
                    keyPath: 'id',
                    autoIncrement: true
                });
                actionsStore.createIndex('timestamp', 'timestamp', { unique: false });
                actionsStore.createIndex('type', 'type', { unique: false });
                actionsStore.createIndex('synced', 'synced', { unique: false });
            }

            // Metadata store - for sync timestamps, etc.
            if (!database.objectStoreNames.contains(STORES.metadata)) {
                database.createObjectStore(STORES.metadata, { keyPath: 'key' });
            }
        };
    });
}

// Generic store operations
async function getFromStore(storeName, key) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.get(key);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result || null);
    });
}

async function getAllFromStore(storeName) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly');
        const store = transaction.objectStore(storeName);
        const request = store.getAll();
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result || []);
    });
}

async function putInStore(storeName, item) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite');
        const store = transaction.objectStore(storeName);
        const request = store.put(item);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result);
    });
}

async function deleteFromStore(storeName, key) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite');
        const store = transaction.objectStore(storeName);
        const request = store.delete(key);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(true);
    });
}

async function clearStore(storeName) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readwrite');
        const store = transaction.objectStore(storeName);
        const request = store.clear();
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(true);
    });
}

async function getByIndex(storeName, indexName, value) {
    const database = await initDatabase();
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(storeName, 'readonly');
        const store = transaction.objectStore(storeName);
        const index = store.index(indexName);
        const request = index.getAll(value);
        request.onerror = () => reject(request.error);
        request.onsuccess = () => resolve(request.result || []);
    });
}

// Asset-specific operations
window.iamsOffline = {
    // Initialize
    init: async function() {
        await initDatabase();
        return true;
    },

    // Assets
    saveAssets: async function(assets) {
        const database = await initDatabase();
        return new Promise((resolve, reject) => {
            const transaction = database.transaction(STORES.assets, 'readwrite');
            const store = transaction.objectStore(STORES.assets);

            assets.forEach(asset => {
                store.put(asset);
            });

            transaction.oncomplete = () => resolve(true);
            transaction.onerror = () => reject(transaction.error);
        });
    },

    getAssets: async function() {
        return await getAllFromStore(STORES.assets);
    },

    getAsset: async function(id) {
        return await getFromStore(STORES.assets, id);
    },

    getAssetByTag: async function(assetTag) {
        const results = await getByIndex(STORES.assets, 'assetTag', assetTag);
        return results.length > 0 ? results[0] : null;
    },

    saveAsset: async function(asset) {
        return await putInStore(STORES.assets, asset);
    },

    deleteAsset: async function(id) {
        return await deleteFromStore(STORES.assets, id);
    },

    clearAssets: async function() {
        return await clearStore(STORES.assets);
    },

    // Pending Actions
    addPendingAction: async function(action) {
        action.timestamp = new Date().toISOString();
        action.synced = false;
        return await putInStore(STORES.pendingActions, action);
    },

    getPendingActions: async function() {
        const actions = await getAllFromStore(STORES.pendingActions);
        return actions.filter(a => !a.synced).sort((a, b) =>
            new Date(a.timestamp) - new Date(b.timestamp)
        );
    },

    getPendingActionCount: async function() {
        const actions = await this.getPendingActions();
        return actions.length;
    },

    markActionSynced: async function(id) {
        const action = await getFromStore(STORES.pendingActions, id);
        if (action) {
            action.synced = true;
            action.syncedAt = new Date().toISOString();
            return await putInStore(STORES.pendingActions, action);
        }
        return false;
    },

    deletePendingAction: async function(id) {
        return await deleteFromStore(STORES.pendingActions, id);
    },

    clearSyncedActions: async function() {
        const actions = await getAllFromStore(STORES.pendingActions);
        const database = await initDatabase();
        return new Promise((resolve, reject) => {
            const transaction = database.transaction(STORES.pendingActions, 'readwrite');
            const store = transaction.objectStore(STORES.pendingActions);

            actions.filter(a => a.synced).forEach(action => {
                store.delete(action.id);
            });

            transaction.oncomplete = () => resolve(true);
            transaction.onerror = () => reject(transaction.error);
        });
    },

    // Metadata
    setMetadata: async function(key, value) {
        return await putInStore(STORES.metadata, { key, value, updatedAt: new Date().toISOString() });
    },

    getMetadata: async function(key) {
        const result = await getFromStore(STORES.metadata, key);
        return result ? result.value : null;
    },

    getLastSyncTime: async function() {
        return await this.getMetadata('lastSyncTime');
    },

    setLastSyncTime: async function(time) {
        return await this.setMetadata('lastSyncTime', time || new Date().toISOString());
    },

    // Network status
    isOnline: function() {
        return navigator.onLine;
    },

    // Storage info
    getStorageInfo: async function() {
        const assets = await this.getAssets();
        const pendingActions = await this.getPendingActions();
        const lastSync = await this.getLastSyncTime();

        return {
            assetCount: assets.length,
            pendingActionCount: pendingActions.length,
            lastSyncTime: lastSync,
            isOnline: navigator.onLine
        };
    }
};

// Network status event handling with debouncing for mobile
let networkStatusCallback = null;
let networkDebounceTimer = null;
let lastReportedStatus = null;
const NETWORK_DEBOUNCE_MS = 2000; // Wait 2 seconds before reporting status change

function reportNetworkStatus(isOnline) {
    // Only report if status actually changed from last reported
    if (networkStatusCallback && lastReportedStatus !== isOnline) {
        lastReportedStatus = isOnline;
        networkStatusCallback.invokeMethodAsync('OnNetworkStatusChanged', isOnline);
    }
}

function debouncedNetworkChange(isOnline) {
    // Clear any pending debounce
    if (networkDebounceTimer) {
        clearTimeout(networkDebounceTimer);
    }

    // Wait before reporting to avoid rapid toggling on mobile
    networkDebounceTimer = setTimeout(() => {
        reportNetworkStatus(isOnline);
    }, NETWORK_DEBOUNCE_MS);
}

window.iamsOffline.registerNetworkCallback = function(dotNetHelper) {
    networkStatusCallback = dotNetHelper;
    lastReportedStatus = navigator.onLine;

    window.addEventListener('online', () => {
        debouncedNetworkChange(true);
    });

    window.addEventListener('offline', () => {
        // Report offline immediately (important for UX)
        reportNetworkStatus(false);
    });

    return navigator.onLine;
};

window.iamsOffline.unregisterNetworkCallback = function() {
    networkStatusCallback = null;
    if (networkDebounceTimer) {
        clearTimeout(networkDebounceTimer);
        networkDebounceTimer = null;
    }
};

console.log('IAMS Offline Storage module loaded');
