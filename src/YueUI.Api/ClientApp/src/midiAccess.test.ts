import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
})

async function load(requestMIDIAccess: () => Promise<MIDIAccess>) {
  vi.stubGlobal('navigator', { requestMIDIAccess })
  return import('./logic/midiPlayer')
}

function fakeAccess() {
  const listeners = new Set<() => void>()
  return {
    outputs: new Map(),
    addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
    /** What the browser does when a device comes or goes. */
    change: () => listeners.forEach((listener) => listener()),
  } as unknown as MIDIAccess & { change(): void }
}

describe('midiAccess', () => {
  it('asks the browser once and shares the access', async () => {
    const access = fakeAccess()
    const request = vi.fn(() => Promise.resolve(access))
    const { midiAccess, listMidiPorts } = await load(request)

    expect(await midiAccess()).toBe(access)
    expect((await listMidiPorts()).access).toBe(access)
    expect(await midiAccess()).toBe(access)
    expect(request).toHaveBeenCalledTimes(1)
  })

  it('asks again after a refusal', async () => {
    const access = fakeAccess()
    const request = vi
      .fn<() => Promise<MIDIAccess>>()
      .mockRejectedValueOnce(new DOMException('denied', 'SecurityError'))
      .mockResolvedValue(access)
    const { listMidiPorts } = await load(request)

    expect((await listMidiPorts()).reason).toBe('denied')
    expect((await listMidiPorts()).access).toBe(access)
    expect(request).toHaveBeenCalledTimes(2)
  })
})

describe('rescanMidi', () => {
  it('asks again, shares the new access and tells the listeners', async () => {
    const first = fakeAccess()
    const second = fakeAccess()
    const request = vi.fn<() => Promise<MIDIAccess>>().mockResolvedValueOnce(first).mockResolvedValue(second)
    const { midiAccess, rescanMidi, onMidiPortsChanged } = await load(request)
    const changed = vi.fn()
    onMidiPortsChanged(changed)

    expect(await midiAccess()).toBe(first)
    expect(await rescanMidi()).toBe(second)
    expect(await midiAccess()).toBe(second)
    expect(changed).toHaveBeenCalledTimes(1)

    // Only the current access reports devices coming and going.
    first.change()
    expect(changed).toHaveBeenCalledTimes(1)
    second.change()
    expect(changed).toHaveBeenCalledTimes(2)
  })

  it('keeps the access the page has when asking again fails', async () => {
    const first = fakeAccess()
    const request = vi
      .fn<() => Promise<MIDIAccess>>()
      .mockResolvedValueOnce(first)
      .mockRejectedValue(new DOMException('denied', 'SecurityError'))
    const { midiAccess, rescanMidi } = await load(request)

    expect(await midiAccess()).toBe(first)
    await expect(rescanMidi()).rejects.toThrow()
    expect(await midiAccess()).toBe(first)
  })

  it('stops calling a listener that unsubscribed', async () => {
    const access = fakeAccess()
    const { midiAccess, onMidiPortsChanged } = await load(() => Promise.resolve(access))
    const changed = vi.fn()
    const stop = onMidiPortsChanged(changed)
    await midiAccess()
    stop()
    access.change()
    expect(changed).not.toHaveBeenCalled()
  })
})
