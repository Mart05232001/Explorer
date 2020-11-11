﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityExplorer.Helpers;
using UnityEngine;
using UnityExplorer.Inspectors.Reflection;
using UnityExplorer.UI.Shared;
using System.Reflection;
using UnityExplorer.UI;
using UnityEngine.UI;
using UnityExplorer.Config;

namespace UnityExplorer.Inspectors
{
    // TODO:
    // - Filters
    // - Helper tools for Target object (for UnityEngine.Objects, Components, Textures, and maybe a general ToString helper)

    public class ReflectionInspector : InspectorBase
    {
        public override string TabLabel => m_targetTypeShortName;

        internal readonly Type m_targetType;
        internal readonly string m_targetTypeShortName;

        // all cached members of the target
        internal CacheMember[] m_allMembers;
        // filtered members based on current filters
        internal CacheMember[] m_membersFiltered;
        // actual shortlist of displayed members
        internal readonly CacheMember[] m_displayedMembers = new CacheMember[ModConfig.Instance.Default_Page_Limit];

        // UI members

        private GameObject m_content;
        public override GameObject Content
        {
            get => m_content;
            set => m_content = value;
        }

        internal PageHandler m_pageHandler;
        internal SliderScrollbar m_sliderScroller;
        internal GameObject m_scrollContent;
        
        // Blacklists
        private static readonly HashSet<string> s_typeAndMemberBlacklist = new HashSet<string>
        {
            // these cause a crash
            "Type.DeclaringMethod",
            "Rigidbody2D.Cast",
            "Collider2D.Cast",
            "Collider2D.Raycast",
        };
        private static readonly HashSet<string> s_methodStartsWithBlacklist = new HashSet<string>
        {
            // these are redundant
            "get_",
            "set_",
        };

        // Ctor

        public ReflectionInspector(object target) : base(target)
        {
            if (this is StaticInspector)
                m_targetType = target as Type;
            else
                m_targetType = ReflectionHelpers.GetActualType(target);

            m_targetTypeShortName = m_targetType.Name;

            ConstructUI();

            CacheMembers(m_targetType);

            RefreshDisplay();
        }

        // Methods

        public override void Update()
        {
            base.Update();

            // todo
        }

        public override void Destroy()
        {
            base.Destroy();

            if (this.Content)
                GameObject.Destroy(this.Content);
        }

        private void OnPageTurned()
        {
            RefreshDisplay();
        }

        public void RefreshDisplay()
        {
            // TODO TEMP
            m_membersFiltered = m_allMembers;

            var members = m_membersFiltered;

            m_pageHandler.ListCount = members.Length;

            int newCount = 0;

            // disable current members
            for (int i = 0; i < m_displayedMembers.Length; i++)
            {
                var mem = m_displayedMembers[i];
                if (mem != null)
                    mem.Disable();
            }

            foreach (var itemIndex in m_pageHandler)
            {
                newCount++;

                // normalized index starting from 0
                var i = itemIndex - m_pageHandler.StartIndex;

                if (itemIndex >= members.Length)
                {
                    break;
                }

                CacheMember member = members[itemIndex];

                member.Enable();

                m_displayedMembers[i] = member;
            }
        }

        public void CacheMembers(Type type)
        {
            var list = new List<CacheMember>();
            var cachedSigs = new HashSet<string>();

            var types = ReflectionHelpers.GetAllBaseTypes(type);

            foreach (var declaringType in types)
            {
                MemberInfo[] infos;
                try
                {
                    infos = declaringType.GetMembers(ReflectionHelpers.CommonFlags);
                }
                catch
                {
                    ExplorerCore.Log($"Exception getting members for type: {declaringType.FullName}");
                    continue;
                }

                var target = Target;
#if CPP
                try
                {
                    target = target.Il2CppCast(declaringType);
                }
                catch //(Exception e)
                {
                    //ExplorerCore.LogWarning("Excepting casting " + target.GetType().FullName + " to " + declaringType.FullName);
                }
#endif

                foreach (var member in infos)
                {
                    try
                    {
                        // make sure member type is Field, Method or Property (4 / 8 / 16)
                        int m = (int)member.MemberType;
                        if (m < 4 || m > 16)
                            continue;

                        var pi = member as PropertyInfo;
                        var mi = member as MethodInfo;

                        if (this is StaticInspector)
                        {
                            if (member is FieldInfo fi && !fi.IsStatic) continue;
                            else if (pi != null && !pi.GetAccessors(true)[0].IsStatic) continue;
                            else if (mi != null && !mi.IsStatic) continue;
                        }

                        // check blacklisted members
                        var sig = $"{member.DeclaringType.Name}.{member.Name}";

                        if (s_typeAndMemberBlacklist.Any(it => sig.Contains(it)))
                            continue;

                        if (s_methodStartsWithBlacklist.Any(it => member.Name.StartsWith(it)))
                            continue;

                        if (mi != null)
                        {
                            AppendParams(mi.GetParameters());
                        }
                        else if (pi != null)
                        {
                            AppendParams(pi.GetIndexParameters());
                        }

                        void AppendParams(ParameterInfo[] _args)
                        {
                            sig += " (";
                            foreach (var param in _args)
                            {
                                sig += $"{param.ParameterType.Name} {param.Name}, ";
                            }
                            sig += ")";
                        }

                        if (cachedSigs.Contains(sig))
                        {
                            continue;
                        }

                        try
                        {
                            //ExplorerCore.Log($"Trying to cache member {sig}...");

                            var cached = CacheFactory.GetCacheObject(member, target, m_scrollContent);

                            if (cached != null)
                            {
                                cachedSigs.Add(sig);
                                list.Add(cached);
                            }
                        }
                        catch (Exception e)
                        {
                            ExplorerCore.LogWarning($"Exception caching member {sig}!");
                            ExplorerCore.Log(e.ToString());
                        }
                    }
                    catch (Exception e)
                    {
                        ExplorerCore.LogWarning($"Exception caching member {member.DeclaringType.FullName}.{member.Name}!");
                        ExplorerCore.Log(e.ToString());
                    }
                }
            }

            m_allMembers = list.ToArray();

            // ExplorerCore.Log("Cached " + m_allMembers.Length + " members");
        }

        #region UI CONSTRUCTION

        internal void ConstructUI()
        {
            var parent = InspectorManager.Instance.m_inspectorContent;
            this.Content = UIFactory.CreateVerticalGroup(parent, new Color(0.15f, 0.15f, 0.15f));
            var mainGroup = Content.GetComponent<VerticalLayoutGroup>();
            mainGroup.childForceExpandHeight = false;
            mainGroup.childForceExpandWidth = true;
            mainGroup.childControlHeight = true;
            mainGroup.childControlWidth = true;
            mainGroup.spacing = 5;
            mainGroup.padding.top = 4;
            mainGroup.padding.left = 4;
            mainGroup.padding.right = 4;
            mainGroup.padding.bottom = 4;

            ConstructTopArea();

            ConstructFilterArea();

            ConstructMemberList();
        }

        internal void ConstructTopArea()
        {
            var typeRowObj = UIFactory.CreateHorizontalGroup(Content, new Color(1, 1, 1, 0));
            var typeRowGroup = typeRowObj.GetComponent<HorizontalLayoutGroup>();
            typeRowGroup.childForceExpandWidth = true;
            typeRowGroup.childForceExpandHeight = true;
            typeRowGroup.childControlHeight = true;
            typeRowGroup.childControlWidth = true;
            var typeRowLayout = typeRowObj.AddComponent<LayoutElement>();
            typeRowLayout.minHeight = 25;
            typeRowLayout.flexibleHeight = 0;
            typeRowLayout.minWidth = 200;
            typeRowLayout.flexibleWidth = 5000;

            var typeLabel = UIFactory.CreateLabel(typeRowObj, TextAnchor.MiddleLeft);
            var typeLabelText = typeLabel.GetComponent<Text>();
            typeLabelText.text = "Type:";
            var typeLabelTextLayout = typeLabel.AddComponent<LayoutElement>();
            typeLabelTextLayout.minWidth = 60;
            typeLabelTextLayout.flexibleWidth = 0;
            typeLabelTextLayout.minHeight = 25;

            var typeLabelInputObj = UIFactory.CreateInputField(typeRowObj);
            var typeLabelInput = typeLabelInputObj.GetComponent<InputField>();
            typeLabelInput.readOnly = true;
            var typeLabelLayout = typeLabelInputObj.AddComponent<LayoutElement>();
            typeLabelLayout.minWidth = 150;
            typeLabelLayout.flexibleWidth = 5000;

            string classColor = SyntaxColors.Class_Instance;
            if (m_targetType.IsSealed && m_targetType.IsAbstract)
                classColor = SyntaxColors.Class_Static;
            else if (m_targetType.IsValueType)
                classColor = SyntaxColors.StructGreen;

            typeLabelInput.text = $"<color=grey>{m_targetType.Namespace}.</color><color={classColor}>{m_targetType.Name}</color>";
        }

        internal void ConstructFilterArea()
        {

        }

        internal void ConstructMemberList()
        {
            var scrollobj = UIFactory.CreateScrollView(Content, out m_scrollContent, out m_sliderScroller, new Color(0.12f, 0.12f, 0.12f));

            var scrollGroup = m_scrollContent.GetComponent<VerticalLayoutGroup>();
            scrollGroup.spacing = 3;

            m_pageHandler = new PageHandler(m_sliderScroller);
            m_pageHandler.ConstructUI(Content);
            m_pageHandler.OnPageChanged += OnPageTurned;
        }

        #endregion
    }
}