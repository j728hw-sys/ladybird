package oldgpu.nativeintel;

import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.IllegalClassFormatException;
import java.lang.instrument.Instrumentation;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.lang.reflect.Method;
import java.util.Locale;
import java.nio.IntBuffer;
import java.security.ProtectionDomain;
import java.util.jar.JarEntry;
import java.util.jar.JarFile;
import org.objectweb.asm.ClassReader;
import org.objectweb.asm.ClassWriter;
import org.objectweb.asm.Opcodes;
import org.objectweb.asm.tree.AbstractInsnNode;
import org.objectweb.asm.tree.ClassNode;
import org.objectweb.asm.tree.IntInsnNode;
import org.objectweb.asm.tree.InsnNode;
import org.objectweb.asm.tree.LdcInsnNode;
import org.objectweb.asm.tree.MethodInsnNode;
import org.objectweb.asm.tree.MethodNode;

/**
 * Minecraft 26.3 compatibility agent for the Windows Intel HD Graphics 2000
 * driver (OpenGL 3.1 / GLSL 1.40).
 *
 * It does not emulate OpenGL and does not render on the CPU. It only relaxes
 * RenderPearl's 3.3 gate and redirects the one GL 3.3 entry point whose ARB
 * extension uses a suffixed symbol on Sandy Bridge.
 */
public final class HD2000NativeAgent {
    private static final String PREFIX = "[HD2000 Native] ";

    public static void premain(String agentArgs, Instrumentation inst) {
        System.err.println(PREFIX + "agent active; waiting for Minecraft 26.3 RenderPearl classes");
        inst.addTransformer(new Transformer(), false);
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 2 || !"--selftest".equals(args[0])) {
            System.err.println("Usage: HD2000NativeAgent --selftest <minecraft-26.3-client.jar>");
            System.exit(2);
            return;
        }

        Transformer transformer = new Transformer();
        String[] targets = {
            Transformer.GL_BACKEND,
            Transformer.GL_DEVICE,
            Transformer.GL_RECOMPILER,
            Transformer.GL_PROGRAM,
            Transformer.GL_TRANSIENT_FALLBACK,
            Transformer.VERTEX_ARRAY_EMULATED
        };

        JarFile jar = new JarFile(args[1]);
        try {
            for (String target : targets) {
                JarEntry entry = jar.getJarEntry(target + ".class");
                if (entry == null) {
                    throw new IllegalStateException("Minecraft 26.3 class missing: " + target);
                }

                byte[] original;
                InputStream in = jar.getInputStream(entry);
                try {
                    original = readAll(in);
                } finally {
                    in.close();
                }

                byte[] patched = transformer.transform(null, target, null, null, original);
                if (patched == null || patched.length == 0) {
                    throw new IllegalStateException("Transformer did not patch: " + target);
                }

                // Parse the result once more to ensure the rewritten class file is structurally valid.
                new ClassReader(patched);
            }
        } finally {
            jar.close();
        }

        System.out.println(PREFIX + "Minecraft 26.3 bytecode self-test PASSED");
    }

    private static byte[] readAll(InputStream in) throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = in.read(buffer)) >= 0) {
            out.write(buffer, 0, read);
        }
        return out.toByteArray();
    }

    /**
     * GlDevice only uses SDL_GL_GetAttribute here to reject contexts below 3.3.
     * The actual LWJGL capabilities are still created from the real Intel 3.1
     * context and therefore remain truthful.
     */
    public static boolean spoofContextVersion(int attr, IntBuffer value) {
        boolean ok = invokeSdlGetAttribute(attr, value);
        if (ok && value != null && (attr == 17 || attr == 18)) {
            value.put(0, 3);
        }
        return ok;
    }

    /**
     * GLSL < 330 loses layout(location=...) in SPIRV-Cross. RenderPearl normally
     * relies on those qualifiers, so bind the names it already generated to the
     * original SPIR-V locations before linking.
     */
    public static void linkLegacyProgram(int programId) {
        try {
            ClassLoader loader = Thread.currentThread().getContextClassLoader();
            Class<?> gl20 = Class.forName("org.lwjgl.opengl.GL20C", true, loader);
            Class<?> gl30 = Class.forName("org.lwjgl.opengl.GL30C", true, loader);

            Method bindAttrib = gl20.getMethod(
                "glBindAttribLocation", int.class, int.class, CharSequence.class);
            Method bindFrag = gl30.getMethod(
                "glBindFragDataLocation", int.class, int.class, CharSequence.class);

            // RenderPearl's GlPipelineRecompiler renames these using the SPIR-V
            // location. Binding non-existing names is harmless per OpenGL.
            for (int location = 0; location < 16; location++) {
                bindAttrib.invoke(null, programId, location,
                    String.format(Locale.ROOT, "_vert_input_%02d", location));
            }
            for (int location = 0; location < 8; location++) {
                bindFrag.invoke(null, programId, location,
                    String.format(Locale.ROOT, "_frag_output_%02d", location));
            }

            Class<?> state = Class.forName(
                "com.mojang.renderpearl.backend.opengl.GlStateManager", true, loader);
            Method link = state.getMethod("glLinkProgram", int.class);
            link.invoke(null, programId);
        } catch (Throwable t) {
            throw new RuntimeException("HD2000 legacy program link failed", t);
        }
    }

    /**
     * Intel Sandy Bridge 9.17.10.4459 is known to be unreliable with mapped
     * buffers. Upload transient data through glBufferSubData instead.
     */
    public static Object uploadGpuNoMap(
            Object transientMemory,
            List<?> data,
            long alignment,
            int usage,
            long minimumAllocation,
            long elementSize) {
        try {
            long totalSize = 0L;
            for (Object item : data) {
                ByteBuffer buffer = ((ByteBuffer) item).duplicate();
                totalSize += buffer.remaining();
                totalSize = alignUp(totalSize, alignment);
            }
            if (totalSize > Integer.MAX_VALUE) {
                throw new IllegalArgumentException("Transient upload exceeds 2GB");
            }

            Method allocateGpu = transientMemory.getClass().getMethod(
                "allocateGpu", long.class, long.class, int.class, long.class, long.class);
            Object slice = allocateGpu.invoke(
                transientMemory, totalSize, alignment, usage, minimumAllocation, elementSize);

            ByteBuffer packed = ByteBuffer.allocateDirect((int) totalSize);
            for (Object item : data) {
                ByteBuffer src = ((ByteBuffer) item).duplicate();
                packed.put(src);
                int aligned = (int) alignUp(packed.position(), alignment);
                while (packed.position() < aligned) {
                    packed.put((byte) 0);
                }
            }
            packed.flip();

            Method bufferMethod = slice.getClass().getMethod("buffer");
            Method offsetMethod = slice.getClass().getMethod("offset");
            Object glBuffer = bufferMethod.invoke(slice);
            long offset = ((Long) offsetMethod.invoke(slice)).longValue();
            Method handleMethod = glBuffer.getClass().getMethod("handle");
            int handle = ((Integer) handleMethod.invoke(glBuffer)).intValue();

            Class<?> c = transientMemory.getClass();
            while (c != null && c.getDeclaredFields().length >= 0) {
                try {
                    java.lang.reflect.Field dsaField = c.getDeclaredField("dsa");
                    dsaField.setAccessible(true);
                    Object dsa = dsaField.get(transientMemory);
                    ClassLoader loader = Thread.currentThread().getContextClassLoader();
                    Class<?> dsaBase = Class.forName(
                        "com.mojang.renderpearl.backend.opengl.DirectStateAccess", true, loader);
                    Method subData = dsaBase.getMethod(
                        "bufferSubData", int.class, long.class, ByteBuffer.class, int.class);
                    subData.invoke(dsa, handle, offset, packed, usage);
                    return slice;
                } catch (NoSuchFieldException ignored) {
                    c = c.getSuperclass();
                }
            }
            throw new NoSuchFieldException("GlTransientMemory.dsa");
        } catch (Throwable t) {
            throw new RuntimeException("HD2000 no-map transient upload failed", t);
        }
    }

    public static List<Object> multiUploadGpuNoMap(
            Object transientMemory, List<?> data, long alignment, int usage) {
        List<Object> result = new ArrayList<Object>(data.size());
        for (Object item : data) {
            ByteBuffer src = ((ByteBuffer) item).duplicate();
            long size = src.remaining();
            List<ByteBuffer> one = new ArrayList<ByteBuffer>(1);
            one.add(src);
            result.add(uploadGpuNoMap(
                transientMemory, one, alignment, usage, size, 1L));
        }
        return result;
    }

    private static long alignUp(long value, long alignment) {
        if (alignment <= 1L) {
            return value;
        }
        long rem = value % alignment;
        return rem == 0L ? value : value + alignment - rem;
    }

    private static boolean invokeSdlGetAttribute(int attr, IntBuffer value) {
        try {
            Class<?> sdlVideo = Class.forName("org.lwjgl.sdl.SDLVideo");
            Method method = sdlVideo.getMethod("SDL_GL_GetAttribute", int.class, IntBuffer.class);
            Object result = method.invoke(null, attr, value);
            return Boolean.TRUE.equals(result);
        } catch (Throwable t) {
            System.err.println(PREFIX + "SDL_GL_GetAttribute bridge failed: " + t);
            return false;
        }
    }

    private static final class Transformer implements ClassFileTransformer {
        static final String GL_BACKEND = "com/mojang/renderpearl/backend/opengl/GlBackend";
        static final String GL_DEVICE = "com/mojang/renderpearl/backend/opengl/GlDevice";
        static final String GL_RECOMPILER = "com/mojang/renderpearl/backend/opengl/GlPipelineRecompiler";
        static final String GL_PROGRAM = "com/mojang/renderpearl/backend/opengl/GlProgram";
        static final String GL_TRANSIENT_FALLBACK = "com/mojang/renderpearl/backend/opengl/GlTransientMemory$Fallback";
        static final String VERTEX_ARRAY_EMULATED = "com/mojang/renderpearl/backend/opengl/VertexArray$Emulated";

        @Override
        public byte[] transform(
                ClassLoader loader,
                String className,
                Class<?> classBeingRedefined,
                ProtectionDomain protectionDomain,
                byte[] classfileBuffer) throws IllegalClassFormatException {
            if (className == null) {
                return null;
            }

            try {
                if (GL_BACKEND.equals(className)) {
                    return patchGlBackend(classfileBuffer);
                }
                if (GL_DEVICE.equals(className)) {
                    return patchGlDevice(classfileBuffer);
                }
                if (GL_RECOMPILER.equals(className)) {
                    return patchGlPipelineRecompiler(classfileBuffer);
                }
                if (GL_PROGRAM.equals(className)) {
                    return patchGlProgram(classfileBuffer);
                }
                if (GL_TRANSIENT_FALLBACK.equals(className)) {
                    return patchTransientFallback(classfileBuffer);
                }
                if (VERTEX_ARRAY_EMULATED.equals(className)) {
                    return patchVertexArrayEmulated(classfileBuffer);
                }
            } catch (Throwable t) {
                System.err.println(PREFIX + "failed to patch " + className + ": " + t);
                t.printStackTrace(System.err);
            }
            return null;
        }

        private byte[] patchGlBackend(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if (!"org/lwjgl/sdl/SDLVideo".equals(call.owner)
                            || !"SDL_GL_SetAttribute".equals(call.name)
                            || !"(II)Z".equals(call.desc)) {
                        continue;
                    }

                    AbstractInsnNode valueNode = previousReal(insn);
                    AbstractInsnNode attrNode = previousReal(valueNode);
                    Integer attr = intValue(attrNode);
                    Integer value = intValue(valueNode);
                    if (attr == null || value == null) {
                        continue;
                    }

                    // SDL3: 18=minor, 19=flags, 20=profile.
                    // Request a plain 3.1 context: no forward-compatible flag,
                    // and no core-profile requirement (profiles start at GL 3.2).
                    if (attr == 18 && value == 3) {
                        replaceInt(method, valueNode, 1);
                        changed++;
                    } else if (attr == 19 && value != 0) {
                        replaceInt(method, valueNode, 0);
                        changed++;
                    } else if (attr == 20 && value != 0) {
                        replaceInt(method, valueNode, 0);
                        changed++;
                    }
                }
            }

            if (changed < 3) {
                throw new IllegalStateException("Expected at least 3 GlBackend context patches, got " + changed);
            }
            System.err.println(PREFIX + "GlBackend: " + changed + " context attributes patched for OpenGL 3.1");
            return write(cn);
        }

        private byte[] patchGlDevice(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if ("org/lwjgl/sdl/SDLVideo".equals(call.owner)
                            && "SDL_GL_GetAttribute".equals(call.name)
                            && "(ILjava/nio/IntBuffer;)Z".equals(call.desc)) {
                        call.owner = "oldgpu/nativeintel/HD2000NativeAgent";
                        call.name = "spoofContextVersion";
                        call.itf = false;
                        call.setOpcode(Opcodes.INVOKESTATIC);
                        changed++;
                    }
                }
            }

            if (changed < 2) {
                throw new IllegalStateException("Expected 2 GlDevice SDL version reads, got " + changed);
            }
            System.err.println(PREFIX + "GlDevice: " + changed + " version checks bridged; real GL capabilities stay untouched");
            return write(cn);
        }

        private byte[] patchGlPipelineRecompiler(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if (!"org/lwjgl/util/spvc/Spvc".equals(call.owner)
                            || !"spvc_compiler_options_set_uint".equals(call.name)) {
                        continue;
                    }

                    AbstractInsnNode valueNode = previousReal(insn);
                    AbstractInsnNode optionNode = previousReal(valueNode);
                    Integer option = intValue(optionNode);
                    Integer value = intValue(valueNode);

                    // SPVC_COMPILER_OPTION_GLSL_VERSION (0x02000008)
                    if (option != null && option == 33554440 && value != null && value == 330) {
                        replaceInt(method, valueNode, 140);
                        changed++;
                    }
                }
            }

            if (changed < 1) {
                throw new IllegalStateException("GLSL 330 target was not found in GlPipelineRecompiler");
            }
            System.err.println(PREFIX + "GlPipelineRecompiler: " + changed + " GLSL target(s) changed 330 -> 140");
            return write(cn);
        }

        private byte[] patchGlProgram(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if ("com/mojang/renderpearl/backend/opengl/GlStateManager".equals(call.owner)
                            && "glLinkProgram".equals(call.name)
                            && "(I)V".equals(call.desc)) {
                        // The program ID is already on the operand stack.
                        call.owner = "oldgpu/nativeintel/HD2000NativeAgent";
                        call.name = "linkLegacyProgram";
                        call.itf = false;
                        call.setOpcode(Opcodes.INVOKESTATIC);
                        changed++;
                    }
                }
            }

            if (changed < 1) {
                throw new IllegalStateException("GlProgram.glLinkProgram call was not found");
            }
            System.err.println(PREFIX + "GlProgram: " + changed
                + " link call(s) patched to bind legacy attrib/fragment locations");
            return write(cn);
        }

        private byte[] patchTransientFallback(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                if ("uploadGpu".equals(method.name)
                        && "(Ljava/util/List;JIJJ)Lcom/mojang/renderpearl/api/buffers/GpuBufferSlice;".equals(method.desc)) {
                    method.instructions.clear();
                    method.tryCatchBlocks.clear();
                    method.localVariables = null;
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ALOAD, 0));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ALOAD, 1));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.LLOAD, 2));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ILOAD, 4));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.LLOAD, 5));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.LLOAD, 7));
                    method.instructions.add(new MethodInsnNode(
                        Opcodes.INVOKESTATIC,
                        "oldgpu/nativeintel/HD2000NativeAgent",
                        "uploadGpuNoMap",
                        "(Ljava/lang/Object;Ljava/util/List;JIJJ)Ljava/lang/Object;",
                        false));
                    method.instructions.add(new org.objectweb.asm.tree.TypeInsnNode(
                        Opcodes.CHECKCAST, "com/mojang/renderpearl/api/buffers/GpuBufferSlice"));
                    method.instructions.add(new InsnNode(Opcodes.ARETURN));
                    method.maxStack = 9;
                    method.maxLocals = 9;
                    changed++;
                } else if ("multiUploadGpu".equals(method.name)
                        && "(Ljava/util/List;JI)Ljava/util/List;".equals(method.desc)) {
                    method.instructions.clear();
                    method.tryCatchBlocks.clear();
                    method.localVariables = null;
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ALOAD, 0));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ALOAD, 1));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.LLOAD, 2));
                    method.instructions.add(new org.objectweb.asm.tree.VarInsnNode(Opcodes.ILOAD, 4));
                    method.instructions.add(new MethodInsnNode(
                        Opcodes.INVOKESTATIC,
                        "oldgpu/nativeintel/HD2000NativeAgent",
                        "multiUploadGpuNoMap",
                        "(Ljava/lang/Object;Ljava/util/List;JI)Ljava/util/List;",
                        false));
                    method.instructions.add(new InsnNode(Opcodes.ARETURN));
                    method.maxStack = 5;
                    method.maxLocals = 5;
                    changed++;
                }
            }

            if (changed < 2) {
                throw new IllegalStateException("Expected 2 transient upload methods, got " + changed);
            }
            System.err.println(PREFIX + "GlTransientMemory.Fallback: mapped uploads disabled for Sandy Bridge");
            return write(cn);
        }

        private byte[] patchVertexArrayEmulated(byte[] bytes) {
            ClassNode cn = read(bytes);
            int changed = 0;

            for (MethodNode method : cn.methods) {
                for (AbstractInsnNode insn = method.instructions.getFirst(); insn != null; insn = insn.getNext()) {
                    if (!(insn instanceof MethodInsnNode)) {
                        continue;
                    }
                    MethodInsnNode call = (MethodInsnNode) insn;
                    if ("org/lwjgl/opengl/GL33C".equals(call.owner)
                            && "glVertexAttribDivisor".equals(call.name)
                            && "(II)V".equals(call.desc)) {
                        // Intel's Sandy Bridge Windows driver exposes
                        // GL_ARB_instanced_arrays with the ARB-suffixed symbol.
                        call.owner = "org/lwjgl/opengl/ARBInstancedArrays";
                        call.name = "glVertexAttribDivisorARB";
                        call.itf = false;
                        call.setOpcode(Opcodes.INVOKESTATIC);
                        changed++;
                    }
                }
            }

            if (changed < 1) {
                throw new IllegalStateException("glVertexAttribDivisor call was not found in VertexArray.Emulated");
            }
            System.err.println(PREFIX + "VertexArray.Emulated: " + changed + " instancing call(s) redirected to ARB");
            return write(cn);
        }

        private static ClassNode read(byte[] bytes) {
            ClassReader reader = new ClassReader(bytes);
            ClassNode node = new ClassNode();
            reader.accept(node, 0);
            return node;
        }

        private static byte[] write(ClassNode node) {
            ClassWriter writer = new ClassWriter(0);
            node.accept(writer);
            return writer.toByteArray();
        }

        private static AbstractInsnNode previousReal(AbstractInsnNode node) {
            if (node == null) {
                return null;
            }
            AbstractInsnNode p = node.getPrevious();
            while (p != null && (p.getType() == AbstractInsnNode.LABEL
                    || p.getType() == AbstractInsnNode.LINE
                    || p.getType() == AbstractInsnNode.FRAME)) {
                p = p.getPrevious();
            }
            return p;
        }

        private static Integer intValue(AbstractInsnNode node) {
            if (node instanceof InsnNode) {
                int op = node.getOpcode();
                if (op >= Opcodes.ICONST_M1 && op <= Opcodes.ICONST_5) {
                    return op - Opcodes.ICONST_0;
                }
            }
            if (node instanceof IntInsnNode) {
                return ((IntInsnNode) node).operand;
            }
            if (node instanceof LdcInsnNode) {
                Object cst = ((LdcInsnNode) node).cst;
                if (cst instanceof Integer) {
                    return (Integer) cst;
                }
            }
            return null;
        }

        private static void replaceInt(MethodNode method, AbstractInsnNode oldNode, int value) {
            AbstractInsnNode replacement;
            if (value >= -1 && value <= 5) {
                replacement = new InsnNode(Opcodes.ICONST_0 + value);
            } else if (value >= Byte.MIN_VALUE && value <= Byte.MAX_VALUE) {
                replacement = new IntInsnNode(Opcodes.BIPUSH, value);
            } else if (value >= Short.MIN_VALUE && value <= Short.MAX_VALUE) {
                replacement = new IntInsnNode(Opcodes.SIPUSH, value);
            } else {
                replacement = new LdcInsnNode(value);
            }
            method.instructions.set(oldNode, replacement);
        }
    }
}
