import { afterEach, describe, expect, it, vi } from 'vitest'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.resetModules()
})

async function load(requestMIDIAccess: () => Promise<MIDIAccess>) {
  vi.stubGlobal('navigator', { requestMIDIAccess })
  return import('./logic/midiPlayer')
}

describe('midiAccess', () => {
  it('asks the browser once and shares the access', async () => {
    const access = { outputs: new Map() } as unknown as MIDIAccess
    const request = vi.fn(() => Promise.resolve(access))
    const { midiAccess, listMidiPorts } = await load(request)

    expect(await midiAccess()).toBe(access)
    expect((await listMidiPorts()).access).toBe(access)
    expect(await midiAccess()).toBe(access)
    expect(request).toHaveBeenCalledTimes(1)
  })

  it('asks again after a refusal', async () => {
    const access = { outputs: new Map() } as unknown as MIDIAccess
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
